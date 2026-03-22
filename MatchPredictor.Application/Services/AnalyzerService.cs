using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hangfire;
using MatchPredictor.Application.Helpers;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Services;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MatchPredictor.Application.Services;

public class AnalyzerService : IAnalyzerService
{
    private const int DataRetentionDays = 90;
    private const int ScrapingLogRetentionDays = 2;
    private const string ScoreUpdateRunKind = "score_update";
    private const string ScoreUpdateRecentEventName = "score_update_recent";
    private const string ScoreUpdateBackfillEventName = "score_update_backfill";
    private const string AiScoreRuntimeEventName = "source_runtime_aiscore";
    private const string SofaScoreRuntimeEventName = "source_runtime_sofascore";
    private static readonly TimeSpan FutureFixtureSettlementTolerance = TimeSpan.Zero;
    private static readonly Regex ScoreRegex = new("(\\d+)\\D+(\\d+)", RegexOptions.Compiled);
    private static readonly Regex HandicapRegex = new(
        "^(Home|Away)\\s+([+-]?\\d+(?:\\.\\d+)?)\\s+Sets$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IDataAnalyzerService _dataAnalyzerService;
    private readonly IWebScraperService _webScraperService;
    private readonly ApplicationDbContext _dbContext;
    private readonly IExtractFromExcel _excelExtract;
    private readonly ICalibrationService _calibrationService;
    private readonly IThresholdTuningService _thresholdTuningService;
    private readonly ISourceMarketPricingService _sourceMarketPricingService;
    private readonly AiScoreSourceHealthTracker _aiScoreSourceHealthTracker;
    private readonly SofaScoreSourceHealthTracker _sofaScoreSourceHealthTracker;
    private readonly PredictionSettings _predictionSettings;
    private readonly ILogger<AnalyzerService> _logger;

    public AnalyzerService(
        IDataAnalyzerService dataAnalyzerService,
        IWebScraperService webScraperService,
        ApplicationDbContext dbContext,
        IExtractFromExcel excelExtract,
        ICalibrationService calibrationService,
        IThresholdTuningService thresholdTuningService,
        ISourceMarketPricingService sourceMarketPricingService,
        AiScoreSourceHealthTracker aiScoreSourceHealthTracker,
        SofaScoreSourceHealthTracker sofaScoreSourceHealthTracker,
        IOptions<PredictionSettings> predictionOptions,
        ILogger<AnalyzerService> logger)
    {
        _dataAnalyzerService = dataAnalyzerService;
        _webScraperService = webScraperService;
        _dbContext = dbContext;
        _excelExtract = excelExtract;
        _calibrationService = calibrationService;
        _thresholdTuningService = thresholdTuningService;
        _sourceMarketPricingService = sourceMarketPricingService;
        _aiScoreSourceHealthTracker = aiScoreSourceHealthTracker;
        _sofaScoreSourceHealthTracker = sofaScoreSourceHealthTracker;
        _predictionSettings = predictionOptions.Value;
        _logger = logger;
    }

    [AutomaticRetry(OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    public async Task ExtractDataAndSyncDatabaseAsync(int predictionDayOffset = 0, string? runReason = null)
    {
        var targetLocalDateTime = DateTimeProvider.GetLocalTime().Date.AddDays(predictionDayOffset);
        var targetLocalDate = DateOnly.FromDateTime(targetLocalDateTime);
        var targetDateString = DateTimeProvider.FormatLocalDate(targetLocalDate);
        var normalizedRunReason = string.IsNullOrWhiteSpace(runReason)
            ? predictionDayOffset > 0 ? "prewarm" : "scheduled-sync"
            : runReason.Trim();

        _logger.LogInformation(
            "Starting tennis data extraction for {TargetDate} (offset {DayOffset}).",
            targetDateString,
            predictionDayOffset);

        try
        {
            List<MatchData> scrapedMatches;
            try
            {
                await _webScraperService.ScrapeMatchDataAsync();
                scrapedMatches = _excelExtract.ExtractMatchDatasetFromFile(targetLocalDateTime).ToList();
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Browser scraping is disabled", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    ex,
                    "Browser download is unavailable for {TargetDate}. Falling back to any already-downloaded workbook on disk.",
                    targetDateString);
                scrapedMatches = _excelExtract.ExtractMatchDatasetFromFile(targetLocalDateTime).ToList();
            }

            foreach (var match in scrapedMatches)
            {
                ApplyCanonicalMatchFields(match);
            }

            var existingMatches = await _dbContext.MatchDatas
                .Where(match => match.MatchLocalDate == targetLocalDate)
                .ToListAsync();
            var existingByKey = existingMatches
                .GroupBy(match => BuildMatchStorageKey(match.FixtureKey, match.MatchLocalTime))
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            foreach (var match in scrapedMatches)
            {
                var storageKey = BuildMatchStorageKey(match.FixtureKey, match.MatchLocalTime);
                if (existingByKey.TryGetValue(storageKey, out var existing))
                {
                    CopyMatchData(existing, match);
                    continue;
                }

                await _dbContext.MatchDatas.AddAsync(match);
                existingByKey[storageKey] = match;
            }

            await _dbContext.SaveChangesAsync();
            await LogScrapingStatusAsync("data_sync", "Success", $"Tennis data sync completed successfully for {targetDateString} ({scrapedMatches.Count} matches).");

            BackgroundJob.Enqueue<IAnalyzerService>(service =>
                service.GeneratePredictionsAsync(targetDateString, normalizedRunReason));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tennis data extraction failed for {TargetDate}.", targetDateString);
            await LogScrapingStatusAsync("data_sync", "Failed", $"Tennis data sync failed for {targetDateString}: {ex.Message}");
            throw;
        }
    }

    [AutomaticRetry(OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    public async Task GeneratePredictionsAsync(string? targetDate = null, string? runReason = null)
    {
        var targetDateString = ResolveTargetDateString(targetDate);
        var targetLocalDate = ParseTargetLocalDate(targetDateString);
        var normalizedRunReason = string.IsNullOrWhiteSpace(runReason) ? "prediction-generation" : runReason.Trim();
        var runStartedAt = DateTime.UtcNow;

        _logger.LogInformation("Starting tennis prediction generation for {TargetDate}.", targetDateString);

        var run = new PredictionRun
        {
            RunKind = "prediction_generation",
            RunLabel = targetDateString,
            RunReason = normalizedRunReason,
            TargetLocalDate = targetLocalDate,
            StartedAtUtc = runStartedAt,
            Succeeded = false
        };

        await _dbContext.PredictionRuns.AddAsync(run);
        await _dbContext.SaveChangesAsync();

        try
        {
            await BackfillStoredPredictionTimesAsync(30);

            var matches = await _dbContext.MatchDatas
                .Where(match => match.MatchLocalDate == targetLocalDate)
                .ToListAsync();
            foreach (var match in matches)
            {
                ApplyCanonicalMatchFields(match);
            }

            var generationMatches = matches
                .GroupBy(match => BuildMatchStorageKey(match.FixtureKey, match.MatchLocalTime))
                .Select(group => group.OrderBy(match => match.MatchDateTime).First())
                .ToList();

            var rawForecastCandidates = _dataAnalyzerService.BuildForecastCandidates(generationMatches).ToList();
            var forecastCandidates = DeduplicateForecastCandidates(rawForecastCandidates);
            if (forecastCandidates.Count != rawForecastCandidates.Count)
            {
                _logger.LogWarning(
                    "Collapsed {DuplicateCount} duplicate tennis forecast candidates for {TargetDate} before persistence to match the canonical fixture/market uniqueness rules.",
                    rawForecastCandidates.Count - forecastCandidates.Count,
                    targetDateString);
            }
            var publishedCandidates = _dataAnalyzerService.SelectPublishedPredictions(forecastCandidates).ToList();
            var publishedLookup = publishedCandidates.ToDictionary(
                candidate => BuildCandidateKey(candidate),
                candidate => candidate,
                StringComparer.Ordinal);

            var existingCurrentPredictions = await _dbContext.Predictions
                .Where(prediction => prediction.MatchLocalDate == targetLocalDate)
                .Where(prediction => prediction.IsCurrentRevision)
                .ToListAsync();
            var existingCurrentForecasts = await _dbContext.ForecastObservations
                .Where(forecast => forecast.MatchLocalDate == targetLocalDate)
                .Where(forecast => forecast.IsCurrentRevision)
                .ToListAsync();

            var predictionRevisionLookup = existingCurrentPredictions
                .GroupBy(prediction => BuildPredictionRevisionKey(prediction.FixtureKey, prediction.PredictionCategory, prediction.PredictedOutcome))
                .ToDictionary(group => group.Key, group => group.Max(prediction => prediction.RevisionNumber), StringComparer.Ordinal);
            var forecastRevisionLookup = existingCurrentForecasts
                .GroupBy(forecast => BuildForecastRevisionKey(forecast.FixtureKey, forecast.Market))
                .ToDictionary(group => group.Key, group => group.Max(forecast => forecast.RevisionNumber), StringComparer.Ordinal);

            foreach (var prediction in existingCurrentPredictions)
            {
                prediction.IsCurrentRevision = false;
                prediction.SupersededAt = runStartedAt;
            }

            foreach (var forecast in existingCurrentForecasts)
            {
                forecast.IsCurrentRevision = false;
                forecast.SupersededAt = runStartedAt;
            }

            var forecastEntities = new List<ForecastObservation>();
            var predictionEntities = new List<Prediction>();

            foreach (var candidate in forecastCandidates)
            {
                var fixtureIdentity = FixtureIdentityFactory.Build(
                    candidate.HomeTeam,
                    candidate.AwayTeam,
                    candidate.League,
                    candidate.MatchLocalDate,
                    candidate.MatchLocalTime,
                    candidate.MatchDateTime);
                var candidateKey = BuildPredictionRevisionKey(fixtureIdentity.FixtureKey, candidate.PredictionCategory, candidate.PredictedOutcome);
                var forecastKey = BuildForecastRevisionKey(fixtureIdentity.FixtureKey, candidate.Market);
                var nextPredictionRevision = predictionRevisionLookup.TryGetValue(candidateKey, out var predictionRevision)
                    ? predictionRevision + 1
                    : 1;
                var nextForecastRevision = forecastRevisionLookup.TryGetValue(forecastKey, out var forecastRevision)
                    ? forecastRevision + 1
                    : 1;
                predictionRevisionLookup[candidateKey] = nextPredictionRevision;
                forecastRevisionLookup[forecastKey] = nextForecastRevision;

                forecastEntities.Add(new ForecastObservation
                {
                    Date = candidate.Date,
                    Time = candidate.Time,
                    MatchLocalDate = candidate.MatchLocalDate,
                    MatchLocalTime = candidate.MatchLocalTime,
                    MatchDateTime = candidate.MatchDateTime,
                    FixtureKey = fixtureIdentity.FixtureKey,
                    League = fixtureIdentity.League,
                    HomeTeam = fixtureIdentity.HomeTeam,
                    AwayTeam = fixtureIdentity.AwayTeam,
                    Market = candidate.Market,
                    PredictedOutcome = candidate.PredictedOutcome,
                    RawProbability = candidate.RawProbability,
                    CalibratedProbability = candidate.CalibratedProbability,
                    CalibratorUsed = candidate.CalibratorUsed,
                    ThresholdUsed = candidate.ThresholdUsed,
                    ThresholdSource = candidate.ThresholdSource,
                    IsPublished = publishedLookup.ContainsKey(BuildCandidateKey(candidate)),
                    IsLive = false,
                    IsSettled = false,
                    PredictionRunId = run.Id,
                    RunLabel = run.RunLabel,
                    RunReason = run.RunReason,
                    IsCurrentRevision = true,
                    RevisionNumber = nextForecastRevision,
                    CreatedAt = runStartedAt
                });
            }

            foreach (var candidate in publishedCandidates)
            {
                var fixtureIdentity = FixtureIdentityFactory.Build(
                    candidate.HomeTeam,
                    candidate.AwayTeam,
                    candidate.League,
                    candidate.MatchLocalDate,
                    candidate.MatchLocalTime,
                    candidate.MatchDateTime);
                var predictionKey = BuildPredictionRevisionKey(fixtureIdentity.FixtureKey, candidate.PredictionCategory, candidate.PredictedOutcome);
                var nextPredictionRevision = predictionRevisionLookup[predictionKey];

                predictionEntities.Add(new Prediction
                {
                    Date = candidate.Date,
                    Time = candidate.Time,
                    MatchLocalDate = candidate.MatchLocalDate,
                    MatchLocalTime = candidate.MatchLocalTime,
                    MatchDateTime = candidate.MatchDateTime,
                    FixtureKey = fixtureIdentity.FixtureKey,
                    League = fixtureIdentity.League,
                    HomeTeam = fixtureIdentity.HomeTeam,
                    AwayTeam = fixtureIdentity.AwayTeam,
                    PredictionCategory = candidate.PredictionCategory,
                    PredictedOutcome = candidate.PredictedOutcome,
                    RawConfidenceScore = Convert.ToDecimal(candidate.RawProbability, CultureInfo.InvariantCulture),
                    ConfidenceScore = Convert.ToDecimal(candidate.CalibratedProbability, CultureInfo.InvariantCulture),
                    CalibratorUsed = candidate.CalibratorUsed,
                    ThresholdUsed = candidate.ThresholdUsed,
                    ThresholdSource = candidate.ThresholdSource,
                    WasPublished = true,
                    IsLive = false,
                    PredictionRunId = run.Id,
                    RunLabel = run.RunLabel,
                    RunReason = run.RunReason,
                    IsCurrentRevision = true,
                    RevisionNumber = nextPredictionRevision,
                    CreatedAt = runStartedAt
                });
            }

            await _dbContext.ForecastObservations.AddRangeAsync(forecastEntities);
            await _dbContext.Predictions.AddRangeAsync(predictionEntities);
            await _dbContext.SaveChangesAsync();

            await CapturePredictionOddsSnapshotsAsync(run, predictionEntities, PredictionOddsSnapshotKind.Publish);

            run.ForecastCount = forecastEntities.Count;
            run.PublishedPredictionCount = predictionEntities.Count;
            run.Succeeded = true;
            run.CompletedAtUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();

            await LogScrapingStatusAsync("prediction_generation", "Success", $"Generated {predictionEntities.Count} published tennis predictions for {targetDateString}.");
        }
        catch (Exception ex)
        {
            await TryMarkPredictionRunFailedAsync(run, DateTime.UtcNow);
            _logger.LogError(ex, "Tennis prediction generation failed for {TargetDate}.", targetDateString);
            await LogScrapingStatusAsync("prediction_generation", "Failed", $"Prediction generation failed for {targetDateString}: {ex.Message}");
            throw;
        }
    }

    [AutomaticRetry(OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(timeoutInSeconds: 1800)]
    public async Task RunScoreUpdaterAsync(int lookbackDays = 1, string runLabel = "recent")
    {
        var today = DateTimeProvider.GetLocalDate();
        var cutoffLocalDate = today.AddDays(-Math.Max(lookbackDays, 0));
        var nowUtc = DateTime.UtcNow;
        var normalizedRunLabel = string.IsNullOrWhiteSpace(runLabel) ? "recent" : runLabel.Trim();
        var updateEventName = BuildScoreUpdateEventName(normalizedRunLabel);

        _logger.LogInformation("Starting tennis score updater ({RunLabel}) for fixtures since {CutoffDate}.", normalizedRunLabel, cutoffLocalDate);

        try
        {
            var currentPredictions = await _dbContext.Predictions
                .Where(prediction => prediction.IsCurrentRevision)
                .Where(prediction => prediction.MatchLocalDate >= cutoffLocalDate)
                .ToListAsync();
            var currentForecasts = await _dbContext.ForecastObservations
                .Where(forecast => forecast.IsCurrentRevision)
                .Where(forecast => forecast.MatchLocalDate >= cutoffLocalDate)
                .ToListAsync();

            if (currentPredictions.Count == 0 && currentForecasts.Count == 0)
            {
                await LogScrapingStatusAsync(
                    updateEventName,
                    "Success",
                    $"No current tennis predictions or forecasts required a {normalizedRunLabel} score update.",
                    runKind: ScoreUpdateRunKind,
                    runLabel: normalizedRunLabel,
                    payload: new
                    {
                        runLabel = normalizedRunLabel,
                        lookbackDays,
                        predictions = 0,
                        forecasts = 0,
                        flashScoreRows = 0,
                        aiScoreRows = 0,
                        sofaScoreRows = 0,
                        updatedPredictions = 0,
                        updatedForecasts = 0
                    });
                await PersistSourceRuntimeSnapshotsAsync(normalizedRunLabel);
                return;
            }

            var fixtureRequests = currentForecasts
            .Select(forecast => new SofaScoreFixtureRequest
            {
                League = forecast.League,
                HomeTeam = forecast.HomeTeam,
                AwayTeam = forecast.AwayTeam,
                MatchLocalDate = forecast.MatchLocalDate,
                ScheduledMatchTimeUtc = forecast.MatchDateTime,
                FixtureKey = forecast.FixtureKey
            })
            .Concat(currentPredictions.Select(prediction => new SofaScoreFixtureRequest
            {
                League = prediction.League,
                HomeTeam = prediction.HomeTeam,
                AwayTeam = prediction.AwayTeam,
                MatchLocalDate = prediction.MatchLocalDate,
                ScheduledMatchTimeUtc = prediction.MatchDateTime,
                FixtureKey = prediction.FixtureKey
            }))
            .GroupBy(request => request.FixtureKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

            List<MatchScore> flashScores = [];
            List<MatchScore> tennisScores = [];
            List<AiScoreMatchScore> aiScores = [];
            List<SofaScoreMatchScore> sofaScores = [];
            string flashStage = "http-html";
            string flashStatus = "Empty";
            string flashMessage = "FlashScore did not return any tennis score rows.";
            string tennisScoresStage = "http-html-results";
            string tennisScoresStatus = "Empty";
            string tennisScoresMessage = "tennisscores.mobi did not return any tennis score rows.";
            string aiStatus = "Empty";
            string aiMessage = "AiScore did not return any tennis score rows.";
            string sofaStatus = "Empty";
            string sofaMessage = "SofaScore did not return any tennis score rows.";

            try
            {
                flashScores = await _webScraperService.ScrapeMatchScoresAsync();
                if (flashScores.Count > 0)
                {
                    flashStatus = "Success";
                    flashMessage = $"FlashScore returned {flashScores.Count} tennis score row(s).";
                }
                else
                {
                    flashMessage = "FlashScore returned 0 tennis score rows for the current updater window.";
                }
            }
            catch (Exception ex)
            {
                flashStatus = "Failed";
                flashMessage = $"FlashScore tennis scrape failed: {ex.Message}";
                _logger.LogWarning(ex, "FlashScore tennis scrape failed.");
            }

            try
            {
                tennisScores = await _webScraperService.ScrapeTennisScoresMatchScoresAsync();
                if (tennisScores.Count > 0)
                {
                    tennisScoresStatus = "Success";
                    tennisScoresMessage = $"tennisscores.mobi returned {tennisScores.Count} tennis score row(s).";
                }
                else
                {
                    tennisScoresMessage = "tennisscores.mobi returned 0 tennis score rows for the current updater window.";
                }
            }
            catch (Exception ex)
            {
                tennisScoresStatus = "Failed";
                tennisScoresMessage = $"tennisscores.mobi tennis scrape failed: {ex.Message}";
                _logger.LogWarning(ex, "tennisscores.mobi tennis scrape failed.");
            }

            try
            {
                _aiScoreSourceHealthTracker.RecordAttempt("tennis-score-update", "Fetching tennis results from AiScore.");
                aiScores = await _webScraperService.ScrapeAiScoreMatchScoresAsync();
                var aiSnapshot = _aiScoreSourceHealthTracker.GetSnapshot();
                if (aiScores.Count > 0)
                {
                    _aiScoreSourceHealthTracker.RecordSuccess("tennis-score-update", aiScores.Count, "Fetched tennis results from AiScore.");
                    aiStatus = "Success";
                    aiMessage = $"AiScore returned {aiScores.Count} tennis score row(s).";
                }
                else
                {
                    aiStatus = string.IsNullOrWhiteSpace(aiSnapshot.Status) ? "Empty" : aiSnapshot.Status;
                    aiMessage = string.IsNullOrWhiteSpace(aiSnapshot.LastDetail)
                        ? "AiScore returned 0 tennis score rows for this updater run."
                        : $"AiScore returned 0 tennis score rows. {aiSnapshot.LastDetail}";
                }
            }
            catch (Exception ex)
            {
                _aiScoreSourceHealthTracker.RecordFailure("tennis-score-update", ex.Message);
                aiStatus = "Failed";
                aiMessage = $"AiScore tennis scrape failed: {ex.Message}";
                _logger.LogWarning(ex, "AiScore tennis scrape failed.");
            }

            try
            {
                _sofaScoreSourceHealthTracker.RecordAttempt("tennis-score-update", "Fetching tennis results from SofaScore.");
                sofaScores = await _webScraperService.ScrapeSofaScoreMatchScoresAsync(fixtureRequests);
                var sofaSnapshot = _sofaScoreSourceHealthTracker.GetSnapshot();
                if (sofaScores.Count > 0)
                {
                    _sofaScoreSourceHealthTracker.RecordSuccess("tennis-score-update", sofaScores.Count, fixtureRequests.Count, sofaScores.Count, "Fetched tennis results from SofaScore.");
                    sofaStatus = "Success";
                    sofaMessage = $"SofaScore returned {sofaScores.Count} targeted tennis score row(s) across {fixtureRequests.Count} fixture request(s).";
                }
                else
                {
                    sofaStatus = string.IsNullOrWhiteSpace(sofaSnapshot.Status) ? "Empty" : sofaSnapshot.Status;
                    sofaMessage = string.IsNullOrWhiteSpace(sofaSnapshot.LastDetail)
                        ? $"SofaScore returned 0 targeted tennis score rows across {fixtureRequests.Count} fixture request(s)."
                        : $"SofaScore returned 0 targeted tennis score rows across {fixtureRequests.Count} fixture request(s). {sofaSnapshot.LastDetail}";
                }
            }
            catch (Exception ex)
            {
                _sofaScoreSourceHealthTracker.RecordFailure("tennis-score-update", ex.Message);
                sofaStatus = "Failed";
                sofaMessage = $"SofaScore tennis scrape failed: {ex.Message}";
                _logger.LogWarning(ex, "SofaScore tennis scrape failed.");
            }

            var combinedListingScores = flashScores
                .Concat(tennisScores)
                .ToList();

            await UpsertStoredScoresAsync(combinedListingScores, aiScores, sofaScores);

            var storedScoreWindowStartUtc = nowUtc.AddDays(-Math.Max(lookbackDays + 2, 3));
            var storedFlashScores = await _dbContext.MatchScores
                .Where(score => score.MatchTime >= storedScoreWindowStartUtc)
                .ToListAsync();
            var storedAiScores = await _dbContext.AiScoreMatchScores
                .Where(score => score.MatchTime >= storedScoreWindowStartUtc)
                .ToListAsync();
            var storedSofaScores = await _dbContext.SofaScoreMatchScores
                .Where(score => score.MatchTime >= storedScoreWindowStartUtc)
                .ToListAsync();

            var scoreCandidates = BuildScoreCandidates(storedFlashScores, storedAiScores, storedSofaScores);
            var updatedPredictions = 0;
            foreach (var prediction in currentPredictions)
            {
                if (prediction.MatchDateTime.HasValue && prediction.MatchDateTime.Value > nowUtc.Add(FutureFixtureSettlementTolerance))
                {
                    continue;
                }

                var matchedScore = FindBestScore(scoreCandidates, prediction.HomeTeam, prediction.AwayTeam, prediction.League, prediction.MatchDateTime);
                if (matchedScore is null)
                {
                    continue;
                }

                ApplyScoreToPrediction(prediction, matchedScore);
                updatedPredictions++;
            }

            var updatedForecasts = 0;
            foreach (var forecast in currentForecasts)
            {
                if (forecast.MatchDateTime.HasValue && forecast.MatchDateTime.Value > nowUtc.Add(FutureFixtureSettlementTolerance))
                {
                    continue;
                }

                var matchedScore = FindBestScore(scoreCandidates, forecast.HomeTeam, forecast.AwayTeam, forecast.League, forecast.MatchDateTime);
                if (matchedScore is null)
                {
                    continue;
                }

                ApplyScoreToForecast(forecast, matchedScore);
                updatedForecasts++;
            }

            if (updatedPredictions > 0 || updatedForecasts > 0)
            {
                await _dbContext.SaveChangesAsync();
                _logger.LogInformation(
                    "Updated {PredictionCount} tennis predictions and {ForecastCount} forecasts from score scrapers.",
                    updatedPredictions,
                    updatedForecasts);
            }

            await LogScrapingStatusAsync(
                "source_flashscore",
                flashStatus,
                flashMessage,
                sourceName: "FlashScore",
                stage: flashStage,
                runKind: ScoreUpdateRunKind,
                runLabel: normalizedRunLabel,
                payload: new
                {
                    rows = flashScores.Count,
                    liveRows = flashScores.Count(score => score.IsLive)
                });

            await LogScrapingStatusAsync(
                "source_tennisscores",
                tennisScoresStatus,
                tennisScoresMessage,
                sourceName: "TennisScoresMobi",
                stage: tennisScoresStage,
                runKind: ScoreUpdateRunKind,
                runLabel: normalizedRunLabel,
                payload: new
                {
                    rows = tennisScores.Count,
                    liveRows = tennisScores.Count(score => score.IsLive)
                });

            await LogScrapingStatusAsync(
                "source_aiscore",
                aiStatus,
                aiMessage,
                sourceName: "AiScore",
                stage: _aiScoreSourceHealthTracker.GetSnapshot().LastStage,
                runKind: ScoreUpdateRunKind,
                runLabel: normalizedRunLabel,
                payload: new
                {
                    rows = aiScores.Count,
                    liveRows = aiScores.Count(score => score.IsLive),
                    detail = _aiScoreSourceHealthTracker.GetSnapshot().LastDetail
                });

            await LogScrapingStatusAsync(
                "source_sofascore",
                sofaStatus,
                sofaMessage,
                sourceName: "SofaScore",
                stage: _sofaScoreSourceHealthTracker.GetSnapshot().LastStage,
                runKind: ScoreUpdateRunKind,
                runLabel: normalizedRunLabel,
                payload: new
                {
                    rows = sofaScores.Count,
                    liveRows = sofaScores.Count(score => score.IsLive),
                    fixtureRequests = fixtureRequests.Count,
                    candidateUrls = _sofaScoreSourceHealthTracker.GetSnapshot().LastCandidateUrlCount,
                    pagesFetched = _sofaScoreSourceHealthTracker.GetSnapshot().LastPageFetchCount,
                    detail = _sofaScoreSourceHealthTracker.GetSnapshot().LastDetail
                });

            await LogScrapingStatusAsync(
                updateEventName,
                "Success",
                $"Tennis {normalizedRunLabel} score update processed {scoreCandidates.Count} source candidate(s) and updated {updatedPredictions} prediction(s) plus {updatedForecasts} forecast row(s).",
                runKind: ScoreUpdateRunKind,
                runLabel: normalizedRunLabel,
                payload: new
                {
                    runLabel = normalizedRunLabel,
                    lookbackDays,
                    predictions = currentPredictions.Count,
                    forecasts = currentForecasts.Count,
                    flashScoreRows = flashScores.Count,
                    tennisScoresRows = tennisScores.Count,
                    aiScoreRows = aiScores.Count,
                    sofaScoreRows = sofaScores.Count,
                    storedFlashScoreRows = storedFlashScores.Count,
                    storedAiScoreRows = storedAiScores.Count,
                    storedSofaScoreRows = storedSofaScores.Count,
                    resolvedCandidates = scoreCandidates.Count,
                    updatedPredictions,
                    updatedForecasts
                });

            await PersistSourceRuntimeSnapshotsAsync(normalizedRunLabel);
        }
        catch (Exception ex)
        {
            _dbContext.ChangeTracker.Clear();
            _logger.LogError(ex, "Tennis score updater ({RunLabel}) failed.", normalizedRunLabel);
            await LogScrapingStatusAsync(
                updateEventName,
                "Failed",
                $"Tennis {normalizedRunLabel} score update failed: {ex.Message}",
                runKind: ScoreUpdateRunKind,
                runLabel: normalizedRunLabel,
                payload: new
                {
                    runLabel = normalizedRunLabel,
                    lookbackDays,
                    cutoffLocalDate,
                    error = ex.Message
                });
            await PersistSourceRuntimeSnapshotsAsync(normalizedRunLabel);
            throw;
        }
    }

    [AutomaticRetry(OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(timeoutInSeconds: 1800)]
    public async Task CaptureClosingLineSnapshotsAsync(int lookaheadMinutes = 15)
    {
        var nowUtc = DateTime.UtcNow;
        var upperBound = nowUtc.AddMinutes(Math.Max(lookaheadMinutes, 1));
        var currentPredictions = await _dbContext.Predictions
            .Where(prediction => prediction.IsCurrentRevision)
            .Where(prediction => prediction.PredictionCategory == "MatchWinner")
            .Where(prediction => prediction.MatchDateTime.HasValue &&
                                 prediction.MatchDateTime >= nowUtc &&
                                 prediction.MatchDateTime <= upperBound)
            .ToListAsync();

        if (currentPredictions.Count == 0)
        {
            return;
        }

        var run = new PredictionRun
        {
            RunKind = "closing_line_snapshot",
            RunLabel = DateTimeProvider.FormatLocalDate(DateTimeProvider.GetLocalDate()),
            RunReason = "closing-snapshot",
            TargetLocalDate = DateTimeProvider.GetLocalDate(),
            StartedAtUtc = nowUtc,
            Succeeded = false
        };

        await _dbContext.PredictionRuns.AddAsync(run);
        await _dbContext.SaveChangesAsync();

        try
        {
            await CapturePredictionOddsSnapshotsAsync(run, currentPredictions, PredictionOddsSnapshotKind.Close);
            run.PublishedPredictionCount = currentPredictions.Count;
            run.Succeeded = true;
            run.CompletedAtUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
            await LogScrapingStatusAsync("closing_line_snapshot", "Success", $"Captured tennis closing-line snapshots for {currentPredictions.Count} match-winner picks.");
        }
        catch (Exception ex)
        {
            run.Succeeded = false;
            run.CompletedAtUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
            _logger.LogError(ex, "Closing-line snapshot capture failed.");
            await LogScrapingStatusAsync("closing_line_snapshot", "Failed", $"Closing-line snapshot capture failed: {ex.Message}");
            throw;
        }
    }

    [AutomaticRetry(OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(timeoutInSeconds: 1800)]
    public async Task RunDailyAnalysisAsync()
    {
        _logger.LogInformation("Starting daily tennis analysis.");

        try
        {
            await BackfillStoredPredictionTimesAsync(30);
            await _calibrationService.RebuildProfilesAsync();
            await _thresholdTuningService.RebuildProfilesAsync();
            await RebuildSourceQualityProfilesAsync();
            await LogScrapingStatusAsync("source_quality", "Success", "Rebuilt tennis source-quality profiles from persisted scraper score rows.");
            await LogScrapingStatusAsync("daily_analysis", "Success", "Tennis calibration, threshold, and source-quality analysis completed successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Daily tennis analysis failed.");
            await LogScrapingStatusAsync("source_quality", "Failed", $"Source-quality rebuild failed: {ex.Message}");
            await LogScrapingStatusAsync("daily_analysis", "Failed", $"Daily tennis analysis failed: {ex.Message}");
            throw;
        }
    }

    public async Task CleanupOldPredictionsAndMatchDataAsync()
    {
        var cutoffLocalDate = DateTimeProvider.GetLocalTime().AddDays(-DataRetentionDays).Date;
        var cutoffUtc = DateTime.SpecifyKind(cutoffLocalDate, DateTimeKind.Utc);

        await _dbContext.Predictions
            .Where(prediction => prediction.CreatedAt.Date < cutoffLocalDate)
            .ExecuteDeleteAsync();

        await _dbContext.ForecastObservations
            .Where(forecast => forecast.CreatedAt.Date < cutoffLocalDate)
            .ExecuteDeleteAsync();

        var allMatchData = await _dbContext.MatchDatas.ToListAsync();
        var oldMatchData = allMatchData
            .Where(match => DateTime.TryParse(match.Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate) && parsedDate.Date < cutoffLocalDate)
            .ToList();
        if (oldMatchData.Count > 0)
        {
            _dbContext.MatchDatas.RemoveRange(oldMatchData);
            await _dbContext.SaveChangesAsync();
        }

        await _dbContext.AiScoreMatchScores
            .Where(score => score.MatchTime < cutoffUtc)
            .ExecuteDeleteAsync();

        await _dbContext.MatchScores
            .Where(score => score.MatchTime < cutoffUtc)
            .ExecuteDeleteAsync();

        await _dbContext.SofaScoreMatchScores
            .Where(score => score.MatchTime < cutoffUtc)
            .ExecuteDeleteAsync();

        await _dbContext.PredictionOddsSnapshots
            .Where(snapshot => snapshot.CapturedAtUtc < cutoffUtc)
            .ExecuteDeleteAsync();

        var logCutoff = DateTimeProvider.GetLocalTime().AddDays(-ScrapingLogRetentionDays);
        await _dbContext.ScrapingLogs
            .Where(log => log.Timestamp < logCutoff)
            .ExecuteDeleteAsync();
    }

    public async Task BackfillStoredPredictionTimesAsync(int lookbackDays = 90)
    {
        var today = DateTimeProvider.GetLocalDate();
        var dates = Enumerable.Range(0, Math.Max(lookbackDays, 0) + 1)
            .Select(offset => today.AddDays(-offset))
            .ToHashSet();

        var matches = await _dbContext.MatchDatas
            .Where(match => match.MatchLocalDate.HasValue && dates.Contains(match.MatchLocalDate.Value))
            .ToListAsync();
        if (matches.Count == 0)
        {
            return;
        }

        foreach (var match in matches)
        {
            ApplyCanonicalMatchFields(match);
        }

        var matchLookup = matches
            .GroupBy(match => BuildMatchStorageKey(match.FixtureKey, match.MatchLocalTime))
            .ToDictionary(group => group.Key, group => group.OrderBy(match => match.MatchDateTime).First(), StringComparer.Ordinal);

        var predictions = await _dbContext.Predictions
            .Where(prediction => dates.Contains(prediction.MatchLocalDate))
            .ToListAsync();
        var forecasts = await _dbContext.ForecastObservations
            .Where(forecast => dates.Contains(forecast.MatchLocalDate))
            .ToListAsync();

        var updatedPredictions = 0;
        foreach (var prediction in predictions)
        {
            var predictionKey = BuildMatchStorageKey(
                string.IsNullOrWhiteSpace(prediction.FixtureKey)
                    ? FixtureIdentityFactory.Build(
                        prediction.HomeTeam,
                        prediction.AwayTeam,
                        prediction.League,
                        prediction.MatchLocalDate,
                        prediction.MatchLocalTime,
                        prediction.MatchDateTime).FixtureKey
                    : prediction.FixtureKey,
                prediction.MatchLocalTime);

            if (matchLookup.TryGetValue(predictionKey, out var matched) && ApplyStoredTime(prediction, matched))
            {
                updatedPredictions++;
            }
        }

        var updatedForecasts = 0;
        foreach (var forecast in forecasts)
        {
            var forecastKey = BuildMatchStorageKey(
                string.IsNullOrWhiteSpace(forecast.FixtureKey)
                    ? FixtureIdentityFactory.Build(
                        forecast.HomeTeam,
                        forecast.AwayTeam,
                        forecast.League,
                        forecast.MatchLocalDate,
                        forecast.MatchLocalTime,
                        forecast.MatchDateTime).FixtureKey
                    : forecast.FixtureKey,
                forecast.MatchLocalTime);

            if (matchLookup.TryGetValue(forecastKey, out var matched) && ApplyStoredTime(forecast, matched))
            {
                updatedForecasts++;
            }
        }

        if (updatedPredictions > 0 || updatedForecasts > 0)
        {
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation(
                "Repaired stored kickoff fields for {PredictionCount} tennis predictions and {ForecastCount} forecasts.",
                updatedPredictions,
                updatedForecasts);
        }
    }

    private async Task CapturePredictionOddsSnapshotsAsync(
        PredictionRun run,
        IReadOnlyCollection<Prediction> predictions,
        PredictionOddsSnapshotKind snapshotKind)
    {
        if (predictions.Count == 0)
        {
            return;
        }

        IReadOnlyList<SourceMarketFixture> sourceFixtures;
        try
        {
            sourceFixtures = await _sourceMarketPricingService.GetTodaySourceMarketFixturesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch live tennis pricing while capturing odds snapshots.");
            return;
        }

        var existingSnapshots = await _dbContext.PredictionOddsSnapshots
            .Where(snapshot => snapshot.SnapshotKind == snapshotKind)
            .Where(snapshot => predictions.Select(prediction => prediction.Id).Contains(snapshot.PredictionId))
            .ToListAsync();
        var existingLookup = existingSnapshots.ToDictionary(
            snapshot => BuildSnapshotKey(snapshot.PredictionId, snapshot.SourceName, snapshot.SnapshotKind),
            snapshot => snapshot,
            StringComparer.Ordinal);

        foreach (var prediction in predictions)
        {
            var match = new MatchData
            {
                League = prediction.League,
                Tournament = prediction.League,
                HomeTeam = prediction.HomeTeam,
                AwayTeam = prediction.AwayTeam,
                MatchDateTime = prediction.MatchDateTime,
                MatchLocalDate = prediction.MatchLocalDate,
                MatchLocalTime = prediction.MatchLocalTime,
                FixtureKey = prediction.FixtureKey
            };
            var sourceFixture = SourceMarketFixtureMatcher.FindBestFixture(
                sourceFixtures,
                prediction.HomeTeam,
                prediction.AwayTeam,
                prediction.League,
                prediction.MatchDateTime);

            if (MarketQuoteResolver.TryResolve(match, prediction, sourceFixture, out var quote) == false)
            {
                continue;
            }

            var snapshotKey = BuildSnapshotKey(prediction.Id, quote.SourceName, snapshotKind);
            if (existingLookup.TryGetValue(snapshotKey, out var existing))
            {
                existing.DecimalOdds = quote.DecimalOdds;
                existing.ImpliedProbability = quote.ImpliedProbability;
                existing.OddsDerivationSource = quote.OddsDerivationSource;
                existing.CapturedAtUtc = DateTime.UtcNow;
                continue;
            }

            await _dbContext.PredictionOddsSnapshots.AddAsync(new PredictionOddsSnapshot
            {
                PredictionId = prediction.Id,
                PredictionRunId = run.Id,
                SourceName = quote.SourceName,
                Market = prediction.PredictionCategory,
                Outcome = prediction.PredictedOutcome,
                DecimalOdds = quote.DecimalOdds,
                ImpliedProbability = quote.ImpliedProbability,
                OddsDerivationSource = quote.OddsDerivationSource,
                SnapshotKind = snapshotKind,
                CapturedAtUtc = DateTime.UtcNow
            });
        }

        await _dbContext.SaveChangesAsync();
    }

    private async Task UpsertStoredScoresAsync(
        IReadOnlyCollection<MatchScore> flashScores,
        IReadOnlyCollection<AiScoreMatchScore> aiScores,
        IReadOnlyCollection<SofaScoreMatchScore> sofaScores)
    {
        if (flashScores.Count > 0)
        {
            var existingFlashScores = await _dbContext.MatchScores
                .Where(score => score.MatchTime >= DateTime.UtcNow.AddDays(-7))
                .ToListAsync();
            var flashLookup = existingFlashScores
                .GroupBy(score => BuildScoreStorageKey(score.HomeTeam, score.AwayTeam, score.League, score.MatchTime), StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(GetStoredScorePriority).First(),
                    StringComparer.Ordinal);

            foreach (var score in flashScores)
            {
                var storageKey = BuildScoreStorageKey(score.HomeTeam, score.AwayTeam, score.League, score.MatchTime);
                if (flashLookup.TryGetValue(storageKey, out var existing))
                {
                    CopyMatchScore(existing, score);
                    continue;
                }

                await _dbContext.MatchScores.AddAsync(score);
                flashLookup[storageKey] = score;
            }
        }

        if (aiScores.Count > 0)
        {
            var existingAiScores = await _dbContext.AiScoreMatchScores
                .Where(score => score.MatchTime >= DateTime.UtcNow.AddDays(-7))
                .ToListAsync();
            var aiLookup = existingAiScores
                .GroupBy(score => BuildScoreStorageKey(score.HomeTeam, score.AwayTeam, score.League, score.MatchTime), StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(GetStoredScorePriority).First(),
                    StringComparer.Ordinal);

            foreach (var score in aiScores)
            {
                var storageKey = BuildScoreStorageKey(score.HomeTeam, score.AwayTeam, score.League, score.MatchTime);
                if (aiLookup.TryGetValue(storageKey, out var existing))
                {
                    CopyAiScore(existing, score);
                    continue;
                }

                await _dbContext.AiScoreMatchScores.AddAsync(score);
                aiLookup[storageKey] = score;
            }
        }

        if (sofaScores.Count > 0)
        {
            var existingSofaScores = await _dbContext.SofaScoreMatchScores
                .Where(score => score.MatchTime >= DateTime.UtcNow.AddDays(-7))
                .ToListAsync();
            var sofaLookup = existingSofaScores
                .GroupBy(score => BuildScoreStorageKey(score.HomeTeam, score.AwayTeam, score.League, score.MatchTime), StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(GetStoredScorePriority).First(),
                    StringComparer.Ordinal);

            foreach (var score in sofaScores)
            {
                var storageKey = BuildScoreStorageKey(score.HomeTeam, score.AwayTeam, score.League, score.MatchTime);
                if (sofaLookup.TryGetValue(storageKey, out var existing))
                {
                    CopySofaScore(existing, score);
                    continue;
                }

                await _dbContext.SofaScoreMatchScores.AddAsync(score);
                sofaLookup[storageKey] = score;
            }
        }

        if (flashScores.Count > 0 || aiScores.Count > 0 || sofaScores.Count > 0)
        {
            await _dbContext.SaveChangesAsync();
        }
    }

    private async Task RebuildSourceQualityProfilesAsync()
    {
        var recentCutoff = DateTime.UtcNow.AddDays(-14);
        var flashScores = await _dbContext.MatchScores
            .AsNoTracking()
            .Where(score => score.MatchTime >= recentCutoff)
            .ToListAsync();
        var aiScores = await _dbContext.AiScoreMatchScores
            .AsNoTracking()
            .Where(score => score.MatchTime >= recentCutoff)
            .ToListAsync();
        var sofaScores = await _dbContext.SofaScoreMatchScores
            .AsNoTracking()
            .Where(score => score.MatchTime >= recentCutoff)
            .ToListAsync();

        await _dbContext.SourceQualityProfiles.ExecuteDeleteAsync();

        var profiles = new List<SourceQualityProfile>
        {
            BuildSourceQualityProfile("FlashScore", flashScores.Select(score => new SourceQualitySeed(score.League, score.MatchTime, score.IsLive, score.NormalizedScoreline))),
            BuildSourceQualityProfile("AiScore", aiScores.Select(score => new SourceQualitySeed(score.League, score.MatchTime, score.IsLive, score.NormalizedScoreline))),
            BuildSourceQualityProfile("SofaScore", sofaScores.Select(score => new SourceQualitySeed(score.League, score.MatchTime, score.IsLive, score.NormalizedScoreline)))
        };

        profiles = profiles.Where(profile => profile.SampleCount > 0).ToList();
        if (profiles.Count > 0)
        {
            await _dbContext.SourceQualityProfiles.AddRangeAsync(profiles);
            await _dbContext.SaveChangesAsync();
        }
    }

    private async Task LogScrapingStatusAsync(
        string eventName,
        string status,
        string message,
        string? sourceName = null,
        string? stage = null,
        string? runKind = null,
        string? runLabel = null,
        Guid? predictionRunId = null,
        object? payload = null)
    {
        await _dbContext.ScrapingLogs.AddAsync(new ScrapingLog
        {
            EventName = eventName,
            SourceName = sourceName,
            Stage = stage,
            RunKind = runKind,
            RunLabel = runLabel,
            PredictionRunId = predictionRunId,
            Status = status,
            Message = message,
            PayloadJson = payload is null ? null : JsonSerializer.Serialize(payload),
            Timestamp = DateTime.UtcNow
        });
        await _dbContext.SaveChangesAsync();
    }

    private async Task PersistSourceRuntimeSnapshotsAsync(string runLabel)
    {
        var aiSnapshot = _aiScoreSourceHealthTracker.GetSnapshot();
        await LogScrapingStatusAsync(
            AiScoreRuntimeEventName,
            aiSnapshot.Status,
            BuildAiScoreRuntimeMessage(aiSnapshot),
            sourceName: "AiScore",
            stage: aiSnapshot.LastStage,
            runKind: ScoreUpdateRunKind,
            runLabel: runLabel,
            payload: aiSnapshot);

        var sofaSnapshot = _sofaScoreSourceHealthTracker.GetSnapshot();
        await LogScrapingStatusAsync(
            SofaScoreRuntimeEventName,
            sofaSnapshot.Status,
            BuildSofaScoreRuntimeMessage(sofaSnapshot),
            sourceName: "SofaScore",
            stage: sofaSnapshot.LastStage,
            runKind: ScoreUpdateRunKind,
            runLabel: runLabel,
            payload: sofaSnapshot);
    }

    private static string BuildScoreUpdateEventName(string runLabel)
    {
        return string.Equals(runLabel, "backfill", StringComparison.OrdinalIgnoreCase)
            ? ScoreUpdateBackfillEventName
            : ScoreUpdateRecentEventName;
    }

    private static string BuildAiScoreRuntimeMessage(AiScoreSourceHealthSnapshot snapshot)
    {
        return snapshot.Status switch
        {
            "Healthy" => $"AiScore healthy: {snapshot.LastMatchCount} tennis row(s) returned at stage '{snapshot.LastStage}'.",
            "Empty" => $"AiScore returned no tennis rows at stage '{snapshot.LastStage}'.",
            "Blocked" or "HttpBlocked" => $"AiScore is blocked at stage '{snapshot.LastStage}': {snapshot.LastDetail}",
            "Failed" => $"AiScore failed at stage '{snapshot.LastStage}': {snapshot.LastDetail}",
            _ => snapshot.LastDetail ?? "AiScore runtime snapshot recorded."
        };
    }

    private static string BuildSofaScoreRuntimeMessage(SofaScoreSourceHealthSnapshot snapshot)
    {
        return snapshot.Status switch
        {
            "Healthy" => $"SofaScore healthy: {snapshot.LastMatchCount} row(s), {snapshot.LastCandidateUrlCount} candidate URL(s), {snapshot.LastPageFetchCount} page fetch(es).",
            "Empty" => $"SofaScore returned no targeted rows at stage '{snapshot.LastStage}'.",
            "Blocked" => $"SofaScore was blocked at stage '{snapshot.LastStage}': {snapshot.LastDetail}",
            "Failed" => $"SofaScore failed at stage '{snapshot.LastStage}': {snapshot.LastDetail}",
            _ => snapshot.LastDetail ?? "SofaScore runtime snapshot recorded."
        };
    }

    private async Task TryMarkPredictionRunFailedAsync(PredictionRun run, DateTime completedAtUtc)
    {
        try
        {
            _dbContext.ChangeTracker.Clear();
            run.Succeeded = false;
            run.CompletedAtUtc = completedAtUtc;
            _dbContext.PredictionRuns.Attach(run);
            _dbContext.Entry(run).Property(predictionRun => predictionRun.Succeeded).IsModified = true;
            _dbContext.Entry(run).Property(predictionRun => predictionRun.CompletedAtUtc).IsModified = true;
            await _dbContext.SaveChangesAsync();
        }
        catch (Exception statusEx)
        {
            _logger.LogWarning(statusEx, "Failed to persist the failed state for prediction run {PredictionRunId}.", run.Id);
        }
    }

    private static string ResolveTargetDateString(string? targetDate)
    {
        return string.IsNullOrWhiteSpace(targetDate)
            ? DateTimeProvider.FormatLocalDate(DateTimeProvider.GetLocalDate())
            : targetDate.Trim();
    }

    private static DateOnly ParseTargetLocalDate(string targetDate)
    {
        return DateTimeProvider.ParseLocalDateOrNull(targetDate)
            ?? throw new FormatException($"Unable to parse target date '{targetDate}'.");
    }

    private static void ApplyCanonicalMatchFields(MatchData match)
    {
        var canonicalKickoff = match.MatchDateTime.HasValue
            ? (
                match.MatchLocalDate ?? DateTimeProvider.ConvertUtcToLocalDate(match.MatchDateTime.Value),
                match.MatchLocalTime ?? DateTimeProvider.ConvertUtcToLocalTime(match.MatchDateTime.Value),
                match.MatchDateTime.Value)
            : DateTimeProvider.ParseCanonicalMatchDateTime(match.Date, match.Time);

        match.Date = DateTimeProvider.FormatLocalDate(canonicalKickoff.Item1);
        match.Time = DateTimeProvider.FormatLocalTime(canonicalKickoff.Item2);
        match.MatchLocalDate = canonicalKickoff.Item1;
        match.MatchLocalTime = canonicalKickoff.Item2;
        match.MatchDateTime = canonicalKickoff.Item3;
        match.FixtureKey = FixtureIdentityFactory.FromMatchData(match).FixtureKey;
    }

    private static void CopyMatchData(MatchData target, MatchData source)
    {
        target.Date = source.Date;
        target.Time = source.Time;
        target.MatchLocalDate = source.MatchLocalDate;
        target.MatchLocalTime = source.MatchLocalTime;
        target.MatchDateTime = source.MatchDateTime;
        target.FixtureKey = source.FixtureKey;
        target.SourceMatchId = source.SourceMatchId;
        target.League = source.League;
        target.Tournament = source.Tournament;
        target.Surface = source.Surface;
        target.HomeTeam = source.HomeTeam;
        target.AwayTeam = source.AwayTeam;
        target.HomeWin = source.HomeWin;
        target.AwayWin = source.AwayWin;
        target.OverTwoPointFiveSets = source.OverTwoPointFiveSets;
        target.UnderTwoPointFiveSets = source.UnderTwoPointFiveSets;
        target.SetHandicapLine = source.SetHandicapLine;
        target.SetHandicapLabel = source.SetHandicapLabel;
        target.SetHandicapHome = source.SetHandicapHome;
        target.SetHandicapAway = source.SetHandicapAway;
        target.Score = source.Score;
        target.NormalizedScoreline = source.NormalizedScoreline;
        target.HomeSetsWon = source.HomeSetsWon;
        target.AwaySetsWon = source.AwaySetsWon;
    }

    private static string BuildMatchStorageKey(string fixtureKey, TimeOnly? localTime)
    {
        var timeKey = localTime.HasValue ? localTime.Value.ToString("HH:mm", CultureInfo.InvariantCulture) : "no-time";
        return $"{fixtureKey}|{timeKey}";
    }

    private static string BuildCandidateKey(PredictionCandidate candidate)
    {
        return string.Join(
            "|",
            candidate.MatchLocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            candidate.League.Trim().ToLowerInvariant(),
            candidate.HomeTeam.Trim().ToLowerInvariant(),
            candidate.AwayTeam.Trim().ToLowerInvariant(),
            candidate.PredictionCategory,
            candidate.PredictedOutcome);
    }

    private static string BuildPredictionRevisionKey(string fixtureKey, string category, string predictedOutcome)
    {
        return $"{fixtureKey}|{category}|{predictedOutcome}";
    }

    private static string BuildForecastRevisionKey(string fixtureKey, PredictionMarket market)
    {
        return $"{fixtureKey}|{market}";
    }

    private static List<PredictionCandidate> DeduplicateForecastCandidates(IEnumerable<PredictionCandidate> candidates)
    {
        return candidates
            .Select(candidate =>
            {
                var fixtureIdentity = FixtureIdentityFactory.Build(
                    candidate.HomeTeam,
                    candidate.AwayTeam,
                    candidate.League,
                    candidate.MatchLocalDate,
                    candidate.MatchLocalTime,
                    candidate.MatchDateTime);

                candidate.FixtureKey = fixtureIdentity.FixtureKey;
                return new
                {
                    Candidate = candidate,
                    ForecastKey = BuildForecastRevisionKey(fixtureIdentity.FixtureKey, candidate.Market)
                };
            })
            .GroupBy(item => item.ForecastKey, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(item => item.Candidate.MatchDateTime ?? DateTime.MinValue)
                .ThenByDescending(item => item.Candidate.MatchLocalTime.HasValue)
                .ThenByDescending(item => item.Candidate.CalibratedProbability)
                .ThenByDescending(item => item.Candidate.RawProbability)
                .Select(item => item.Candidate)
                .First())
            .ToList();
    }

    private static string BuildSnapshotKey(int predictionId, string sourceName, PredictionOddsSnapshotKind snapshotKind)
    {
        return $"{predictionId}|{sourceName}|{snapshotKind}";
    }

    private static bool ApplyStoredTime(Prediction prediction, MatchData match)
    {
        var changed = false;
        if (prediction.MatchLocalTime != match.MatchLocalTime)
        {
            prediction.MatchLocalTime = match.MatchLocalTime;
            prediction.Time = match.Time ?? string.Empty;
            changed = true;
        }

        if (prediction.MatchDateTime != match.MatchDateTime)
        {
            prediction.MatchDateTime = match.MatchDateTime;
            changed = true;
        }

        if (!string.Equals(prediction.FixtureKey, match.FixtureKey, StringComparison.Ordinal))
        {
            prediction.FixtureKey = match.FixtureKey;
            changed = true;
        }

        return changed;
    }

    private static bool ApplyStoredTime(ForecastObservation forecast, MatchData match)
    {
        var changed = false;
        if (forecast.MatchLocalTime != match.MatchLocalTime)
        {
            forecast.MatchLocalTime = match.MatchLocalTime;
            forecast.Time = match.Time ?? string.Empty;
            changed = true;
        }

        if (forecast.MatchDateTime != match.MatchDateTime)
        {
            forecast.MatchDateTime = match.MatchDateTime;
            changed = true;
        }

        if (!string.Equals(forecast.FixtureKey, match.FixtureKey, StringComparison.Ordinal))
        {
            forecast.FixtureKey = match.FixtureKey;
            changed = true;
        }

        return changed;
    }

    private static List<ResolvedTennisScore> BuildScoreCandidates(
        IReadOnlyCollection<MatchScore> flashScores,
        IReadOnlyCollection<AiScoreMatchScore> aiScores,
        IReadOnlyCollection<SofaScoreMatchScore> sofaScores)
    {
        var candidates = new List<ResolvedTennisScore>();
        candidates.AddRange(flashScores.Select(score => CreateResolvedScore("FlashScore", score.League, score.HomeTeam, score.AwayTeam, score.MatchTime, score.Score, score.NormalizedScoreline, score.HomeSetsWon, score.AwaySetsWon, score.IsLive)));
        candidates.AddRange(aiScores.Select(score => CreateResolvedScore("AiScore", score.League, score.HomeTeam, score.AwayTeam, score.MatchTime, score.Score, score.NormalizedScoreline, score.HomeSetsWon, score.AwaySetsWon, score.IsLive)));
        candidates.AddRange(sofaScores.Select(score => CreateResolvedScore("SofaScore", score.League, score.HomeTeam, score.AwayTeam, score.MatchTime, score.Score, score.NormalizedScoreline, score.HomeSetsWon, score.AwaySetsWon, score.IsLive)));

        return candidates
            .Where(candidate => candidate.HomeSetsWon.HasValue && candidate.AwaySetsWon.HasValue)
            .GroupBy(BuildResolvedScoreCandidateKey, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(GetResolvedScorePriority)
                .ThenByDescending(candidate => candidate.MatchTimeUtc)
                .First())
            .ToList();
    }

    private static ResolvedTennisScore CreateResolvedScore(
        string sourceName,
        string league,
        string homeTeam,
        string awayTeam,
        DateTime matchTime,
        string score,
        string? normalizedScoreline,
        int? homeSetsWon,
        int? awaySetsWon,
        bool isLive)
    {
        if ((!homeSetsWon.HasValue || !awaySetsWon.HasValue) && TryParseSetScore(normalizedScoreline ?? score, out var parsedHome, out var parsedAway))
        {
            homeSetsWon = parsedHome;
            awaySetsWon = parsedAway;
        }

        var effectiveScore = normalizedScoreline ?? score;
        if (string.IsNullOrWhiteSpace(effectiveScore) && homeSetsWon.HasValue && awaySetsWon.HasValue)
        {
            effectiveScore = $"{homeSetsWon.Value}:{awaySetsWon.Value}";
        }

        return new ResolvedTennisScore(
            sourceName,
            league,
            homeTeam,
            awayTeam,
            matchTime,
            effectiveScore ?? string.Empty,
            normalizedScoreline,
            homeSetsWon,
            awaySetsWon,
            isLive);
    }

    private static ResolvedTennisScore? FindBestScore(
        IReadOnlyCollection<ResolvedTennisScore> candidates,
        string homeTeam,
        string awayTeam,
        string league,
        DateTime? scheduledUtc)
    {
        ResolvedTennisScore? best = null;
        var bestScore = 0d;
        var bestPriority = int.MinValue;

        foreach (var candidate in candidates)
        {
            var homeMatch = ScoreMatchingHelper.GetTeamMatchResult(homeTeam, candidate.HomeTeam, league, candidate.League);
            if (homeMatch.IsMatch == false)
            {
                continue;
            }

            var awayMatch = ScoreMatchingHelper.GetTeamMatchResult(awayTeam, candidate.AwayTeam, league, candidate.League);
            if (awayMatch.IsMatch == false)
            {
                continue;
            }

            var score = homeMatch.Score + awayMatch.Score;
            if (string.IsNullOrWhiteSpace(league) == false && string.IsNullOrWhiteSpace(candidate.League) == false)
            {
                score += ScoreMatchingHelper.GetLeagueMatchScore(league, candidate.League) * 0.2d;
            }

            if (scheduledUtc.HasValue)
            {
                var kickoffDelta = (candidate.MatchTimeUtc - scheduledUtc.Value).Duration();
                var strongTeamMatch = homeMatch.Score >= 0.9d && awayMatch.Score >= 0.9d;
                var sameLocalDate = DateTimeProvider.ConvertUtcToLocalDate(candidate.MatchTimeUtc) ==
                                    DateTimeProvider.ConvertUtcToLocalDate(scheduledUtc.Value);
                if (kickoffDelta > TimeSpan.FromHours(8))
                {
                    if (!(strongTeamMatch && candidate.IsLive == false && sameLocalDate))
                    {
                        continue;
                    }
                }

                score += kickoffDelta <= TimeSpan.FromMinutes(90)
                    ? 0.25d
                    : kickoffDelta <= TimeSpan.FromHours(3)
                        ? 0.1d
                        : 0d;
            }

            score += candidate.IsLive ? 0d : 0.35d;
            score += Math.Min(((candidate.HomeSetsWon ?? 0) + (candidate.AwaySetsWon ?? 0)) * 0.03d, 0.18d);

            var sourcePriority = GetScoreSourcePriority(candidate.SourceName);
            if (score > bestScore ||
                (Math.Abs(score - bestScore) < 0.0001d && sourcePriority > bestPriority))
            {
                bestScore = score;
                best = candidate;
                bestPriority = sourcePriority;
            }
        }

        return bestScore >= 1.55d ? best : null;
    }

    private static void ApplyScoreToPrediction(Prediction prediction, ResolvedTennisScore score)
    {
        prediction.ActualScore = score.NormalizedScoreline ?? score.Score;
        prediction.IsLive = score.IsLive;
        if (score.HomeSetsWon.HasValue == false || score.AwaySetsWon.HasValue == false)
        {
            return;
        }

        if (score.IsLive)
        {
            prediction.ActualOutcome = null;
            return;
        }

        prediction.ActualOutcome = ResolvePredictionOutcome(
            prediction.PredictionCategory,
            prediction.PredictedOutcome,
            score.HomeSetsWon.Value,
            score.AwaySetsWon.Value);
    }

    private static void ApplyScoreToForecast(ForecastObservation forecast, ResolvedTennisScore score)
    {
        forecast.ActualScore = score.NormalizedScoreline ?? score.Score;
        forecast.IsLive = score.IsLive;
        if (score.HomeSetsWon.HasValue == false || score.AwaySetsWon.HasValue == false)
        {
            return;
        }

        if (score.IsLive)
        {
            forecast.IsSettled = false;
            forecast.ActualOutcome = null;
            forecast.OutcomeOccurred = null;
            return;
        }

        forecast.ActualOutcome = ResolveForecastOutcome(forecast.Market, forecast.PredictedOutcome, score.HomeSetsWon.Value, score.AwaySetsWon.Value);
        forecast.OutcomeOccurred = DetermineForecastOutcomeOccurred(forecast.Market, forecast.PredictedOutcome, score.HomeSetsWon.Value, score.AwaySetsWon.Value);
        forecast.IsSettled = forecast.OutcomeOccurred.HasValue;
        if (forecast.IsSettled)
        {
            forecast.SettledAt = DateTime.UtcNow;
        }
    }

    private static string ResolvePredictionOutcome(string category, string predictedOutcome, int homeSetsWon, int awaySetsWon)
    {
        return category switch
        {
            "MatchWinner" => homeSetsWon > awaySetsWon ? "Home Win" : "Away Win",
            "OverUnderSets" => homeSetsWon + awaySetsWon > 2 ? "Over 2.5 Sets" : "Under 2.5 Sets",
            "SetHandicap" => ResolveSetHandicapOutcome(predictedOutcome, homeSetsWon, awaySetsWon),
            _ => string.Empty
        };
    }

    private static string ResolveForecastOutcome(PredictionMarket market, string predictedOutcome, int homeSetsWon, int awaySetsWon)
    {
        return market switch
        {
            PredictionMarket.HomeWin or PredictionMarket.AwayWin => homeSetsWon > awaySetsWon ? "Home Win" : "Away Win",
            PredictionMarket.Over25Sets or PredictionMarket.Under25Sets => homeSetsWon + awaySetsWon > 2 ? "Over 2.5 Sets" : "Under 2.5 Sets",
            PredictionMarket.HomeSetHandicap or PredictionMarket.AwaySetHandicap => ResolveSetHandicapOutcome(predictedOutcome, homeSetsWon, awaySetsWon),
            _ => string.Empty
        };
    }

    private static bool? DetermineForecastOutcomeOccurred(PredictionMarket market, string predictedOutcome, int homeSetsWon, int awaySetsWon)
    {
        return market switch
        {
            PredictionMarket.HomeWin => homeSetsWon > awaySetsWon,
            PredictionMarket.AwayWin => awaySetsWon > homeSetsWon,
            PredictionMarket.Over25Sets => homeSetsWon + awaySetsWon > 2,
            PredictionMarket.Under25Sets => homeSetsWon + awaySetsWon <= 2,
            PredictionMarket.HomeSetHandicap or PredictionMarket.AwaySetHandicap => EvaluateSetHandicapOutcome(predictedOutcome, homeSetsWon, awaySetsWon),
            _ => null
        };
    }

    private static string ResolveSetHandicapOutcome(string predictedOutcome, int homeSetsWon, int awaySetsWon)
    {
        if (TryParseHandicapOutcome(predictedOutcome, out var side, out var line) == false)
        {
            return string.Empty;
        }

        var covered = EvaluateHandicapCoverage(side, line, homeSetsWon, awaySetsWon);
        return covered
            ? FormatSetHandicapOutcome(side, line)
            : FormatSetHandicapOutcome(side == "Home" ? "Away" : "Home", -line);
    }

    private static bool? EvaluateSetHandicapOutcome(string predictedOutcome, int homeSetsWon, int awaySetsWon)
    {
        if (TryParseHandicapOutcome(predictedOutcome, out var side, out var line) == false)
        {
            return null;
        }

        return EvaluateHandicapCoverage(side, line, homeSetsWon, awaySetsWon);
    }

    private static bool EvaluateHandicapCoverage(string side, double line, int homeSetsWon, int awaySetsWon)
    {
        return side.Equals("Home", StringComparison.OrdinalIgnoreCase)
            ? homeSetsWon + line > awaySetsWon
            : awaySetsWon + line > homeSetsWon;
    }

    private static bool TryParseHandicapOutcome(string predictedOutcome, out string side, out double line)
    {
        side = string.Empty;
        line = 0;

        if (string.IsNullOrWhiteSpace(predictedOutcome))
        {
            return false;
        }

        var match = HandicapRegex.Match(predictedOutcome.Trim());
        if (match.Success == false)
        {
            return false;
        }

        side = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(match.Groups[1].Value.ToLowerInvariant());
        return double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out line);
    }

    private static string FormatSetHandicapOutcome(string side, double line)
    {
        return $"{side} {line:+0.0;-0.0} Sets";
    }

    private static bool TryParseSetScore(string? score, out int homeSetsWon, out int awaySetsWon)
    {
        homeSetsWon = 0;
        awaySetsWon = 0;

        if (string.IsNullOrWhiteSpace(score))
        {
            return false;
        }

        var match = ScoreRegex.Match(score.Trim());
        if (match.Success == false)
        {
            return false;
        }

        return int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out homeSetsWon) &&
               int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out awaySetsWon);
    }

    private static string BuildScoreStorageKey(string homeTeam, string awayTeam, string? league, DateTime matchTime)
    {
        var localDate = DateTimeProvider.ConvertUtcToLocalDate(matchTime);
        return string.Join(
            "|",
            ScoreMatchingHelper.CreateTeamLookupKey(homeTeam, league),
            ScoreMatchingHelper.CreateTeamLookupKey(awayTeam, league),
            ScoreMatchingHelper.CreateLeagueLookupKey(league),
            localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    private static string BuildResolvedScoreCandidateKey(ResolvedTennisScore candidate)
    {
        var localDate = DateTimeProvider.ConvertUtcToLocalDate(candidate.MatchTimeUtc);
        return string.Join(
            "|",
            candidate.SourceName,
            ScoreMatchingHelper.CreateTeamLookupKey(candidate.HomeTeam, candidate.League),
            ScoreMatchingHelper.CreateTeamLookupKey(candidate.AwayTeam, candidate.League),
            ScoreMatchingHelper.CreateLeagueLookupKey(candidate.League),
            localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    private static int GetResolvedScorePriority(ResolvedTennisScore candidate)
    {
        var completedSets = (candidate.HomeSetsWon ?? 0) + (candidate.AwaySetsWon ?? 0);
        var scorelineWeight = string.IsNullOrWhiteSpace(candidate.NormalizedScoreline) ? 0 : 1;
        return (candidate.IsLive ? 0 : 100) + (completedSets * 10) + scorelineWeight;
    }

    private static int GetStoredScorePriority(MatchScore score)
    {
        var completedSets = (score.HomeSetsWon ?? 0) + (score.AwaySetsWon ?? 0);
        var scorelineWeight = string.IsNullOrWhiteSpace(score.NormalizedScoreline) ? 0 : 1;
        return (score.IsLive ? 0 : 100) + (completedSets * 10) + scorelineWeight;
    }

    private static int GetStoredScorePriority(AiScoreMatchScore score)
    {
        var completedSets = (score.HomeSetsWon ?? 0) + (score.AwaySetsWon ?? 0);
        var scorelineWeight = string.IsNullOrWhiteSpace(score.NormalizedScoreline) ? 0 : 1;
        return (score.IsLive ? 0 : 100) + (completedSets * 10) + scorelineWeight;
    }

    private static int GetStoredScorePriority(SofaScoreMatchScore score)
    {
        var completedSets = (score.HomeSetsWon ?? 0) + (score.AwaySetsWon ?? 0);
        var scorelineWeight = string.IsNullOrWhiteSpace(score.NormalizedScoreline) ? 0 : 1;
        return (score.IsLive ? 0 : 100) + (completedSets * 10) + scorelineWeight;
    }

    private static void CopyMatchScore(MatchScore target, MatchScore source)
    {
        target.League = source.League;
        target.Score = source.Score;
        target.NormalizedScoreline = source.NormalizedScoreline;
        target.HomeSetsWon = source.HomeSetsWon;
        target.AwaySetsWon = source.AwaySetsWon;
        target.MatchTime = source.MatchTime;
        target.IsLive = source.IsLive;
    }

    private static void CopyAiScore(AiScoreMatchScore target, AiScoreMatchScore source)
    {
        target.League = source.League;
        target.Score = source.Score;
        target.NormalizedScoreline = source.NormalizedScoreline;
        target.HomeSetsWon = source.HomeSetsWon;
        target.AwaySetsWon = source.AwaySetsWon;
        target.MatchTime = source.MatchTime;
        target.IsLive = source.IsLive;
    }

    private static void CopySofaScore(SofaScoreMatchScore target, SofaScoreMatchScore source)
    {
        target.League = source.League;
        target.Score = source.Score;
        target.NormalizedScoreline = source.NormalizedScoreline;
        target.HomeSetsWon = source.HomeSetsWon;
        target.AwaySetsWon = source.AwaySetsWon;
        target.DisplayedScore = source.DisplayedScore;
        target.RegularTimeScore = source.RegularTimeScore;
        target.HalfTimeScore = source.HalfTimeScore;
        target.ExtraTimeScore = source.ExtraTimeScore;
        target.StatusText = source.StatusText;
        target.EventUrl = source.EventUrl;
        target.MatchTime = source.MatchTime;
        target.IsLive = source.IsLive;
    }

    private static int GetScoreSourcePriority(string sourceName)
    {
        return sourceName switch
        {
            "SofaScore" => 3,
            "AiScore" => 2,
            "FlashScore" => 1,
            _ => 0
        };
    }

    private static SourceQualityProfile BuildSourceQualityProfile(string sourceName, IEnumerable<SourceQualitySeed> seeds)
    {
        var seedList = seeds.ToList();
        var sampleCount = seedList.Count;
        var finishedCount = seedList.Count(seed => seed.IsLive == false);
        var exactCount = seedList.Count(seed => seed.IsLive == false && string.IsNullOrWhiteSpace(seed.NormalizedScoreline) == false);
        var liveCount = seedList.Count(seed => seed.IsLive);
        return new SourceQualityProfile
        {
            SourceName = sourceName,
            LeagueKey = "all",
            LeagueLabel = "All Tournaments",
            TimeBucketKey = "all",
            TimeBucketLabel = "All Windows",
            SampleCount = sampleCount,
            FinishedCoverageCount = finishedCount,
            ExactScoreMatchCount = exactCount,
            LiveOnlyCount = liveCount,
            AverageKickoffOffsetMinutes = 0,
            ReliabilityScore = sampleCount > 0 ? finishedCount / (double)sampleCount : 0,
            LastUpdated = DateTime.UtcNow
        };
    }

    private sealed record ResolvedTennisScore(
        string SourceName,
        string League,
        string HomeTeam,
        string AwayTeam,
        DateTime MatchTimeUtc,
        string Score,
        string? NormalizedScoreline,
        int? HomeSetsWon,
        int? AwaySetsWon,
        bool IsLive);

    private sealed record SourceQualitySeed(string League, DateTime MatchTimeUtc, bool IsLive, string? NormalizedScoreline);
}
