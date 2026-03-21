using System.Globalization;
using System.Text.Json;
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
using Npgsql;

namespace MatchPredictor.Application.Services;

public class AnalyzerService  : IAnalyzerService
{
    private const int RecentScoreUpdaterLookbackDays = 1;
    private const int HistoricalScoreBackfillLookbackDays = 14;
    private const double ExactFinishedRepairWindowMinutes = 65d;
    private const double ExtendedExactFinishedRepairWindowMinutes = 240d;
    private static readonly TimeSpan FutureFixtureSettlementTolerance = TimeSpan.Zero;
    private static readonly TimeSpan CurrentRevisionKickoffGrace = TimeSpan.FromMinutes(5);
    private const string DataSyncEventName = "data_sync";
    private const string PredictionGenerationEventName = "prediction_generation";
    private const string DailyAnalysisEventName = "daily_analysis";
    private const string SourceQualityEventName = "source_quality";
    private const string ClosingLineSnapshotEventName = "closing_line_snapshot";
    private const string AiScoreRuntimeEventName = "source_runtime_aiscore";
    private const string SofaScoreRuntimeEventName = "source_runtime_sofascore";

    private readonly IDataAnalyzerService _dataAnalyzerService;
    private readonly IWebScraperService _webScraperService;
    private readonly ApplicationDbContext _dbContext;
    private readonly IExtractFromExcel _excelExtract;
    private readonly ILogger<AnalyzerService> _logger;
    private readonly IRegressionPredictorService _regressionPredictorService;
    private readonly ICalibrationService _calibrationService;
    private readonly IThresholdTuningService _thresholdTuningService;
    private readonly ISourceMarketPricingService _sourceMarketPricingService;
    private readonly AiScoreSourceHealthTracker _aiScoreSourceHealthTracker;
    private readonly SofaScoreSourceHealthTracker _sofaScoreSourceHealthTracker;
    private readonly PredictionSettings _predictionSettings;
    
    public AnalyzerService(
        IDataAnalyzerService dataAnalyzerService,
        IWebScraperService webScraperService,
        ApplicationDbContext dbContext,
        IExtractFromExcel excelExtract,
        IRegressionPredictorService regressionPredictorService,
        ICalibrationService calibrationService,
        IThresholdTuningService thresholdTuningService,
        ISourceMarketPricingService sourceMarketPricingService,
        AiScoreSourceHealthTracker aiScoreSourceHealthTracker,
        IOptions<PredictionSettings> predictionOptions,
        ILogger<AnalyzerService> logger)
        : this(
            dataAnalyzerService,
            webScraperService,
            dbContext,
            excelExtract,
            regressionPredictorService,
            calibrationService,
            thresholdTuningService,
            sourceMarketPricingService,
            aiScoreSourceHealthTracker,
            new SofaScoreSourceHealthTracker(),
            predictionOptions,
            logger)
    {
    }

    public AnalyzerService(
        IDataAnalyzerService dataAnalyzerService,
        IWebScraperService webScraperService,
        ApplicationDbContext dbContext,
        IExtractFromExcel excelExtract,
        IRegressionPredictorService regressionPredictorService,
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
        _regressionPredictorService = regressionPredictorService;
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

        _logger.LogInformation(
            "Starting data extraction process for target date {TargetDate} (day offset {PredictionDayOffset}).",
            targetDateString,
            predictionDayOffset);
        try
        {
            await _webScraperService.ScrapeMatchDataAsync();
            _logger.LogInformation("✅ Web scraping for match data completed successfully.");

            var scraped = _excelExtract.ExtractMatchDatasetFromFile(targetLocalDateTime).ToList();
            IReadOnlyList<SourceMarketFixture> sourceMarketFixtures = [];
            if (predictionDayOffset == 0)
            {
                try
                {
                    sourceMarketFixtures = await _sourceMarketPricingService.GetTodaySourceMarketFixturesAsync();
                    _logger.LogInformation("Fetched {Count} source market fixtures for BTTS enrichment.", sourceMarketFixtures.Count);
                }
                catch (Exception sourceMarketEx)
                {
                    _logger.LogWarning(sourceMarketEx, "⚠️ Failed to fetch source market fixtures for BTTS enrichment. Continuing with workbook-only data.");
                }
            }
            else
            {
                _logger.LogInformation(
                    "Skipping BTTS source market enrichment for target date {TargetDate} because live source pricing is only fetched for the current day.",
                    targetDateString);
            }

            try
            {
                var existingMatches = await _dbContext.MatchDatas
                    .Where(m => m.MatchLocalDate == targetLocalDate)
                    .ToListAsync();

                var existingMatchLookup = existingMatches
                    .GroupBy(m => (
                        FixtureKey: m.FixtureKey ?? string.Empty,
                        Time: m.MatchLocalTime ?? DateTimeProvider.ParseLocalTimeOrNull(m.Time)))
                    .ToDictionary(group => group.Key, group => group.First());

                foreach (var match in scraped)
                {
                    var canonicalKickoff = DateTimeProvider.ParseCanonicalMatchDateTime(match.Date, match.Time);
                    match.Date = DateTimeProvider.FormatLocalDate(canonicalKickoff.localDate);
                    match.Time = DateTimeProvider.FormatLocalTime(canonicalKickoff.localTime);
                    match.MatchLocalDate = canonicalKickoff.localDate;
                    match.MatchLocalTime = canonicalKickoff.localTime;
                    match.MatchDateTime = canonicalKickoff.utcDateTime;
                    match.FixtureKey = FixtureIdentityFactory.FromMatchData(match).FixtureKey;
                    EnrichSourceMarketProbabilities(match, sourceMarketFixtures);

                    var key = (
                        FixtureKey: match.FixtureKey,
                        Time: match.MatchLocalTime);

                    if (existingMatchLookup.TryGetValue(key, out var existing))
                    {
                        existing.HomeWin = match.HomeWin;
                        existing.Draw = match.Draw;
                        existing.AwayWin = match.AwayWin;
                        existing.OverOneGoal = match.OverOneGoal;
                        existing.OverOnePointFive = match.OverOnePointFive;
                        existing.OverTwoGoals = match.OverTwoGoals;
                        existing.OverThreeGoals = match.OverThreeGoals;
                        existing.OverFourGoals = match.OverFourGoals;
                        existing.UnderOnePointFive = match.UnderOnePointFive;
                        existing.UnderTwoGoals = match.UnderTwoGoals;
                        existing.UnderThreeGoals = match.UnderThreeGoals;
                        existing.AhZeroHome = match.AhZeroHome;
                        existing.AhZeroAway = match.AhZeroAway;
                        existing.AhMinusHalfHome = match.AhMinusHalfHome;
                        existing.AhMinusHalfAway = match.AhMinusHalfAway;
                        existing.AhMinusOneHome = match.AhMinusOneHome;
                        existing.AhMinusOneAway = match.AhMinusOneAway;
                        existing.AhPlusHalfHome = match.AhPlusHalfHome;
                        existing.AhPlusHalfAway = match.AhPlusHalfAway;
                        existing.BttsYes = match.BttsYes;
                        existing.BttsNo = match.BttsNo;
                        existing.MatchLocalDate = match.MatchLocalDate;
                        existing.MatchLocalTime = match.MatchLocalTime;
                        existing.MatchDateTime = match.MatchDateTime;
                        existing.FixtureKey = match.FixtureKey;
                    }
                    else
                    {
                        await _dbContext.MatchDatas.AddAsync(match);
                        existingMatchLookup[key] = match;
                    }
                }
                
                await _dbContext.SaveChangesAsync();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "❌ Failed to save match data to database.");
                throw;
            }
            _logger.LogInformation("Extracted and saved {Count} matches to DB for target date {TargetDate}.", scraped.Count, targetDateString);

            // Chain the next job: Generate predictions only after data is successfully synced
            var normalizedRunReason = string.IsNullOrWhiteSpace(runReason)
                ? (predictionDayOffset > 0 ? "prewarm" : "scheduled-sync")
                : runReason.Trim();
            BackgroundJob.Enqueue<IAnalyzerService>(service => service.GeneratePredictionsAsync(targetDateString, normalizedRunReason));
            _logger.LogInformation("Queued GeneratePredictionsAsync background job for target date {TargetDate}.", targetDateString);
            await LogScrapingStatus(
                DataSyncEventName,
                "Success",
                $"✅ Data sync completed successfully for {targetDateString} ({scraped.Count} matches).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ An error occurred during data scraping and sync.");
            await LogScrapingStatus(DataSyncEventName, "Failed", $"Sync Error: {ex.Message}");
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

        _logger.LogInformation("Starting prediction generation process for target date {TargetDate}.", targetDateString);
        try
        {
            await BackfillStoredPredictionTimesAsync(7);
            await BackfillCanonicalFixtureFieldsAsync(7);
            await BackfillDecisionProvenanceAsync(30);

            var matches = await _dbContext.MatchDatas
                .Where(match => match.MatchLocalDate == targetLocalDate)
                .ToListAsync();

            foreach (var match in matches)
            {
                ApplyCanonicalFixtureIdentity(match);
            }

            var generationMatches = DeduplicateMatchesForGeneration(matches, targetDateString);
            IReadOnlyList<SourceMarketFixture> publishPricingFixtures = [];
            if (targetLocalDate == DateTimeProvider.GetLocalDate())
            {
                try
                {
                    publishPricingFixtures = await _sourceMarketPricingService.GetTodaySourceMarketFixturesAsync();
                }
                catch (Exception pricingEx)
                {
                    _logger.LogWarning(pricingEx, "Failed to load live source pricing while generating predictions for {TargetDate}. Publish odds snapshots will fall back to derived pricing.", targetDateString);
                }
            }

            var forecastCandidates = _dataAnalyzerService.BuildForecastCandidates(generationMatches).ToList();
            foreach (var candidate in forecastCandidates)
            {
                ApplyCanonicalFixtureIdentity(candidate);
            }
            forecastCandidates = DeduplicateForecastCandidates(forecastCandidates, targetDateString);

            var publishedCandidates = _dataAnalyzerService.SelectPublishedPredictions(forecastCandidates).ToList();
            foreach (var candidate in publishedCandidates)
            {
                ApplyCanonicalFixtureIdentity(candidate);
            }
            publishedCandidates = DeduplicatePublishedCandidates(publishedCandidates, targetDateString);

            var predictionRun = await CreatePredictionRunAsync(targetLocalDate, normalizedRunReason, forecastCandidates, publishedCandidates);
            await SaveForecastObservations(forecastCandidates, publishedCandidates, predictionRun);
            var savedPredictions = await SavePredictions(forecastCandidates, publishedCandidates, predictionRun);
            await CapturePublishOddsSnapshotsAsync(savedPredictions, generationMatches, publishPricingFixtures);
            predictionRun.Succeeded = true;
            predictionRun.CompletedAtUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
            
            _logger.LogInformation("✅ Predictions calculated and saved successfully for target date {TargetDate}.", targetDateString);
            await LogScrapingStatus(
                PredictionGenerationEventName,
                "Success",
                $"✅ Prediction generation completed successfully for {targetDateString}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ An error occurred during prediction calculations.");
            await LogScrapingStatus(PredictionGenerationEventName, "Failed", $"Prediction Gen Error: {ex.Message}");
            throw;
        }
    }

    [AutomaticRetry(OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(timeoutInSeconds: 1800)]
    public async Task RunScoreUpdaterAsync(int lookbackDays = RecentScoreUpdaterLookbackDays, string runLabel = "recent")
    {
        var normalizedLookbackDays = Math.Clamp(lookbackDays, 0, HistoricalScoreBackfillLookbackDays);
        var normalizedRunLabel = string.IsNullOrWhiteSpace(runLabel) ? "recent" : runLabel.Trim();

        _logger.LogInformation(
            "Starting {RunLabel} score updating process for the last {LookbackDays} day(s).",
            normalizedRunLabel,
            normalizedLookbackDays);
        try
        {
            // Score scraping is non-blocking
            try
            {
                var scores = await _webScraperService.ScrapeMatchScoresAsync();
                _logger.LogInformation("Scraped {Count} match scores from primary source.", scores.Count);
                await SaveMatchScores(scores);
            }
            catch (Exception scoreEx)
            {
                _logger.LogWarning(scoreEx, "❌ Primary score scraping failed.");
            }

            // Secondary score source (AiScore)
            try
            {
                var aiScores = await _webScraperService.ScrapeAiScoreMatchScoresAsync();
                await SaveAiScoreMatchScores(aiScores);
                var aiScoreSnapshot = _aiScoreSourceHealthTracker.GetSnapshot();
                _logger.LogInformation(
                    "AiScore stage finished with status {Status} at stage {Stage}. SofaScore stage will run for unresolved fixtures.",
                    aiScoreSnapshot.Status,
                    aiScoreSnapshot.LastStage ?? "unknown");
            }
            catch (Exception aiScoreEx)
            {
                _logger.LogWarning(aiScoreEx, "❌ AiScore scraping failed.");
            }

            await UpdatePredictionsWithActualResults(normalizedLookbackDays, normalizedRunLabel);
            _logger.LogInformation(
                "✅ Predictions updated with actual results for the {RunLabel} window.",
                normalizedRunLabel);

            await LogScrapingStatus(
                GetScoreUpdateEventName(normalizedRunLabel),
                "Success",
                $"✅ {normalizedRunLabel} score updating completed successfully for the last {normalizedLookbackDays} day(s).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ An error occurred during score updating.");
            await LogScrapingStatus(
                GetScoreUpdateEventName(normalizedRunLabel),
                "Failed",
                $"Score Update Error: {ex.Message}");
            throw;
        }
        finally
        {
            await PersistSourceRuntimeHealthSafelyAsync();
        }
    }

    [AutomaticRetry(OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task CaptureClosingLineSnapshotsAsync(int lookaheadMinutes = 15)
    {
        var normalizedLookaheadMinutes = Math.Clamp(lookaheadMinutes, 1, 60);
        var nowUtc = DateTime.UtcNow;
        var windowEndUtc = nowUtc.AddMinutes(normalizedLookaheadMinutes);

        try
        {
            var candidatePredictions = await _dbContext.Predictions
                .Where(prediction => prediction.IsCurrentRevision && prediction.WasPublished)
                .Where(prediction =>
                    prediction.MatchDateTime.HasValue &&
                    prediction.MatchDateTime.Value >= nowUtc &&
                    prediction.MatchDateTime.Value <= windowEndUtc)
                .OrderBy(prediction => prediction.MatchDateTime)
                .ToListAsync();

            if (candidatePredictions.Count == 0)
            {
                return;
            }

            var predictionIds = candidatePredictions.Select(prediction => prediction.Id).ToList();
            var existingClosePredictionIds = await _dbContext.PredictionOddsSnapshots
                .AsNoTracking()
                .Where(snapshot =>
                    predictionIds.Contains(snapshot.PredictionId) &&
                    snapshot.SnapshotKind == PredictionOddsSnapshotKind.Close)
                .Select(snapshot => snapshot.PredictionId)
                .Distinct()
                .ToListAsync();

            var pendingPredictions = candidatePredictions
                .Where(prediction => !existingClosePredictionIds.Contains(prediction.Id))
                .ToList();

            if (pendingPredictions.Count == 0)
            {
                return;
            }

            IReadOnlyList<SourceMarketFixture> sourceFixtures = [];
            try
            {
                sourceFixtures = await _sourceMarketPricingService.GetTodaySourceMarketFixturesAsync();
            }
            catch (Exception pricingEx)
            {
                _logger.LogWarning(pricingEx, "Failed to load live source pricing for closing-line snapshots. Close snapshots will fall back to derived pricing when possible.");
            }

            var targetDates = pendingPredictions
                .Select(prediction => prediction.MatchLocalDate)
                .Distinct()
                .ToList();

            var matchDatas = await _dbContext.MatchDatas
                .AsNoTracking()
                .Where(match => match.MatchLocalDate.HasValue && targetDates.Contains(match.MatchLocalDate.Value))
                .ToListAsync();

            var createdCount = await SavePredictionOddsSnapshotsAsync(pendingPredictions, matchDatas, sourceFixtures, PredictionOddsSnapshotKind.Close);

            if (createdCount > 0)
            {
                await LogScrapingStatus(
                    ClosingLineSnapshotEventName,
                    "Success",
                    $"✅ Captured {createdCount} closing-line snapshot(s) in the final {normalizedLookaheadMinutes}-minute window.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ An error occurred while capturing closing-line snapshots.");
            await LogScrapingStatus(ClosingLineSnapshotEventName, "Failed", $"Closing-line snapshot error: {ex.Message}");
            throw;
        }
    }

    [AutomaticRetry(OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public async Task RunDailyAnalysisAsync()
    {
        _logger.LogInformation("Starting daily analysis process...");

        try
        {
            await BackfillStoredPredictionTimesAsync();
            _logger.LogInformation("✅ Stored prediction time backfill completed.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Stored prediction time backfill failed, continuing with analytics rebuild.");
        }

        try
        {
            await BackfillDecisionProvenanceAsync();
            _logger.LogInformation("✅ Decision provenance backfill completed.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Decision provenance backfill failed, continuing with analytics rebuild.");
        }

        // ── Step 1: Calibration rebuild (independent) ──
        try
        {
            await RebuildCalibrationProfiles();
            _logger.LogInformation("✅ Calibration rebuild completed.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Calibration rebuild failed, continuing with regression predictions.");
        }

        try
        {
            await RebuildThresholdProfiles();
            _logger.LogInformation("✅ Threshold tuning rebuild completed.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Threshold tuning rebuild failed, continuing with regression predictions.");
        }

        try
        {
            var sourceQualityRebuild = await RebuildSourceQualityProfilesAsync();
            if (sourceQualityRebuild.Completed)
            {
                _logger.LogInformation(
                    "✅ Source quality rebuild completed with {ProfileCount} profile(s).",
                    sourceQualityRebuild.ProfileCount);
                await LogScrapingStatus(
                    SourceQualityEventName,
                    "Success",
                    $"✅ Source quality profiles rebuilt successfully ({sourceQualityRebuild.ProfileCount} profile(s)).");
            }
            else
            {
                _logger.LogWarning(
                    "⚠️ Source quality rebuild skipped: {Reason}",
                    sourceQualityRebuild.StatusMessage);
                await LogScrapingStatus(
                    SourceQualityEventName,
                    "Failed",
                    $"Source quality rebuild skipped: {sourceQualityRebuild.StatusMessage}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Source quality rebuild failed, continuing with regression predictions.");
            await LogScrapingStatus(SourceQualityEventName, "Failed", $"Source quality rebuild error: {ex.Message}");
        }

        // ── Step 2: Download fresh data and generate regression predictions ──
        try
        {
            List<MatchData> scraped;

            // Try to download fresh Excel with the latest odds
            try
            {
                await _webScraperService.ScrapeMatchDataAsync();
                scraped = _excelExtract.ExtractMatchDatasetFromFile().ToList();
                _logger.LogInformation("✅ Fresh Excel downloaded. Extracted {Count} matches for regression.", scraped.Count);
            }
            catch (Exception dlEx)
            {
                _logger.LogWarning(dlEx, "⚠️ Fresh Excel download failed. Falling back to database data.");
                var todayDate = DateTimeProvider.GetLocalDate();
                scraped = await _dbContext.MatchDatas
                    .Where(match => match.MatchLocalDate == todayDate)
                    .ToListAsync();
            }

            if (scraped.Count == 0)
            {
                _logger.LogWarning("⚠️ No match data available for regression predictions. Skipping.");
            }
            else
            {
                var regressionPredictions = _regressionPredictorService.GeneratePredictions(scraped);
                await SaveRegressionPredictions(regressionPredictions);
                _logger.LogInformation("✅ Regression-based predictions saved ({Count} matches, {PredCount} predictions).", scraped.Count, regressionPredictions.Count());
            }

            await LogScrapingStatus(DailyAnalysisEventName, "Success", "✅ Daily analysis completed successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ An error occurred during regression prediction generation.");
            await LogScrapingStatus(DailyAnalysisEventName, "Failed", $"Daily Analysis Error: {ex.Message}");
            throw;
        }
    }

    private static string GetScoreUpdateEventName(string runLabel) =>
        $"score_update_{(string.IsNullOrWhiteSpace(runLabel) ? "recent" : runLabel.Trim().ToLowerInvariant())}";

    private async Task LogScrapingStatus(string eventName, string status, string message)
    {
        try
        {
            var log = new ScrapingLog
            {
                EventName = string.IsNullOrWhiteSpace(eventName) ? "general" : eventName.Trim().ToLowerInvariant(),
                Timestamp = DateTime.UtcNow,
                Status = status,
                Message = message
            };
            await _dbContext.ScrapingLogs.AddAsync(log);
            await _dbContext.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write scraping log.");
        }
    }

    private async Task PersistSourceRuntimeHealthSafelyAsync()
    {
        try
        {
            var aiScoreSnapshot = _aiScoreSourceHealthTracker.GetSnapshot();
            if (HasMeaningfulRuntimeSnapshot(aiScoreSnapshot.Status, aiScoreSnapshot.LastAttemptUtc, aiScoreSnapshot.LastSuccessUtc))
            {
                await PersistSourceRuntimeHealthAsync(AiScoreRuntimeEventName, aiScoreSnapshot.Status, aiScoreSnapshot);
            }

            var sofaScoreSnapshot = _sofaScoreSourceHealthTracker.GetSnapshot();
            if (HasMeaningfulRuntimeSnapshot(sofaScoreSnapshot.Status, sofaScoreSnapshot.LastAttemptUtc, sofaScoreSnapshot.LastSuccessUtc))
            {
                await PersistSourceRuntimeHealthAsync(SofaScoreRuntimeEventName, sofaScoreSnapshot.Status, sofaScoreSnapshot);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist shared source runtime health snapshot.");
        }
    }

    private async Task PersistSourceRuntimeHealthAsync<TSnapshot>(string eventName, string status, TSnapshot snapshot)
    {
        var log = new ScrapingLog
        {
            EventName = eventName,
            Timestamp = DateTime.UtcNow,
            Status = string.IsNullOrWhiteSpace(status) ? "Idle" : status,
            Message = JsonSerializer.Serialize(snapshot)
        };

        await _dbContext.ScrapingLogs.AddAsync(log);
        await _dbContext.SaveChangesAsync();
    }

    private static bool HasMeaningfulRuntimeSnapshot(string? status, DateTime? lastAttemptUtc, DateTime? lastSuccessUtc)
    {
        return lastAttemptUtc.HasValue ||
               lastSuccessUtc.HasValue ||
               !string.Equals(status, "Idle", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveTargetDateString(string? targetDate)
    {
        if (string.IsNullOrWhiteSpace(targetDate))
        {
            return DateTimeProvider.GetLocalTime().ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
        }

        if (DateTime.TryParseExact(
                targetDate.Trim(),
                ["dd-MM-yyyy", "d-M-yyyy", "yyyy-MM-dd"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedDate))
        {
            return parsedDate.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
        }

        throw new FormatException($"Invalid target date format: '{targetDate}'. Expected dd-MM-yyyy.");
    }

    private static DateOnly ParseTargetLocalDate(string targetDate)
    {
        return DateOnly.ParseExact(targetDate, "dd-MM-yyyy", CultureInfo.InvariantCulture);
    }

    private void EnrichSourceMarketProbabilities(MatchData match, IReadOnlyList<SourceMarketFixture> sourceMarketFixtures)
    {
        if (sourceMarketFixtures.Count == 0)
            return;

        var sourceFixture = SourceMarketFixtureMatcher.FindBestFixture(
            sourceMarketFixtures,
            match.HomeTeam,
            match.AwayTeam,
            match.League,
            match.MatchDateTime);

        if (sourceFixture?.BttsYesProbability is not double bttsYesProbability ||
            sourceFixture.BttsNoProbability is not double bttsNoProbability)
        {
            return;
        }

        match.BttsYes = bttsYesProbability;
        match.BttsNo = bttsNoProbability;
        match.NormalizeSourceProbabilities();
    }

    private async Task UpdatePredictionsWithActualResults(int lookbackDays, string runLabel)
    {
        var nowLocal = DateTimeProvider.GetLocalTime();
        var nowUtc = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(nowLocal);
        var earliestSettlementDate = today.AddDays(-lookbackDays);
        var settlementDates = Enumerable.Range(0, lookbackDays + 1)
            .Select(offset => earliestSettlementDate.AddDays(offset))
            .ToHashSet();

        var startOfWindowUtc = DateTimeProvider.ConvertLocalToUtc(earliestSettlementDate.ToDateTime(new TimeOnly(0, 0), DateTimeKind.Unspecified));
        var endOfWindowUtc = DateTimeProvider.ConvertLocalToUtc(today.AddDays(1).ToDateTime(new TimeOnly(0, 0), DateTimeKind.Unspecified));

        var predictionsForSettlement = await _dbContext.Predictions
            .Where(p => settlementDates.Contains(p.MatchLocalDate))
            .ToListAsync();
        var forecastsForSettlement = await _dbContext.ForecastObservations
            .Where(f => settlementDates.Contains(f.MatchLocalDate))
            .ToListAsync();

        var settlementFixtures = BuildSettlementFixtureGroups(predictionsForSettlement, forecastsForSettlement);
        var eligibleSettlementFixtures = settlementFixtures
            .Where(fixture => IsFixtureEligibleForSettlement(fixture, today, nowUtc))
            .ToList();
        var futureSettlementFixtures = settlementFixtures
            .Where(fixture => !IsFixtureEligibleForSettlement(fixture, today, nowUtc))
            .ToList();

        foreach (var fixture in futureSettlementFixtures)
        {
            ClearFutureFixtureSettlement(fixture);
        }

        var eligiblePredictionsForSettlement = eligibleSettlementFixtures
            .SelectMany(fixture => fixture.Predictions)
            .ToList();
        var eligibleForecastsForSettlement = eligibleSettlementFixtures
            .SelectMany(fixture => fixture.Forecasts)
            .ToList();

        foreach (var prediction in eligiblePredictionsForSettlement)
        {
            RepairPredictionOutcomeFromStoredScore(prediction);
        }

        var sourceQualityLookup = await LoadSourceQualityLookupAsync();

        // ── Primary: FlashScore (faster final-status updates) ──
        var scores = await _dbContext.MatchScores
            .Where(s => s.MatchTime >= startOfWindowUtc && s.MatchTime < endOfWindowUtc)
            .ToListAsync();

        var consolidatedFlashScores = ConsolidateFixtureSnapshots(
            scores,
            score => score.HomeTeam,
            score => score.AwayTeam,
            score => score.League,
            score => score.MatchTime,
            score => score.IsLive);

        if (consolidatedFlashScores.Count > 0)
        {
            var flashScoreIndex = new FixtureCandidateIndex<MatchScore>(
                consolidatedFlashScores,
                score => score.HomeTeam,
                score => score.AwayTeam,
                score => score.League,
                score => score.MatchTime);
            var settlementFixtureIndex = new FixtureCandidateIndex<SettlementFixtureGroup>(
                settlementFixtures,
                fixture => fixture.HomeTeam,
                fixture => fixture.AwayTeam,
                fixture => fixture.League,
                fixture => fixture.ScheduledMatchTimeUtc);

            _logger.LogInformation(
                "Matching scores from FlashScore ({CandidateCount} consolidated from {RawCount} rows) against {FixtureCount} fixtures ({PredCount} predictions, {ForecastCount} forecasts) in the {LookbackDays}-day settlement window.",
                consolidatedFlashScores.Count,
                scores.Count,
                eligibleSettlementFixtures.Count,
                eligiblePredictionsForSettlement.Count,
                eligibleForecastsForSettlement.Count,
                lookbackDays);

            var flashMatchedFixtures = 0;
            for (var index = 0; index < eligibleSettlementFixtures.Count; index++)
            {
                var fixture = eligibleSettlementFixtures[index];
                var flashMatch = FindBestFixtureCandidate(
                    flashScoreIndex,
                    fixture.HomeTeam,
                    fixture.AwayTeam,
                    fixture.League,
                    fixture.Date,
                    fixture.ScheduledMatchTimeUtc,
                    score => score.HomeTeam,
                    score => score.AwayTeam,
                    score => score.League,
                    score => score.MatchTime,
                    score => score.IsLive,
                    score => GetSourceQualityReliability(sourceQualityLookup, "FlashScore", score.League, score.MatchTime));

                if (flashMatch != null &&
                    IsReciprocalFixtureMatch(
                        settlementFixtureIndex,
                        fixture,
                        flashMatch,
                        score => score.HomeTeam,
                        score => score.AwayTeam,
                        score => score.League,
                        score => score.MatchTime,
                        score => score.IsLive))
                {
                    ApplyFixtureSettlement(fixture, flashMatch.Score, flashMatch.BTTSLabel, flashMatch.IsLive);
                    flashMatchedFixtures++;
                }

                LogFixtureMatchingProgress("FlashScore", index + 1, eligibleSettlementFixtures.Count, flashMatchedFixtures);
            }
        }

        // ── Fallback: AiScore for any fixtures still missing a score or still marked live ──
        var aiScores = await _dbContext.AiScoreMatchScores
            .Where(s => s.MatchTime >= startOfWindowUtc && s.MatchTime < endOfWindowUtc)
            .ToListAsync();

        var consolidatedAiScores = ConsolidateFixtureSnapshots(
            aiScores,
            score => score.HomeTeam,
            score => score.AwayTeam,
            score => score.League,
            score => score.MatchTime,
            score => score.IsLive);

        var incompleteFixtures = eligibleSettlementFixtures
            .Where(NeedsFixtureSettlementRepair)
            .ToList();
        var incompletePredictions = incompleteFixtures
            .SelectMany(fixture => fixture.Predictions)
            .Where(NeedsPredictionSettlementRepair)
            .ToList();
        var incompleteForecasts = incompleteFixtures
            .SelectMany(fixture => fixture.Forecasts)
            .Where(NeedsForecastSettlementRepair)
            .ToList();

        if (incompleteFixtures.Count > 0 && consolidatedAiScores.Count > 0)
        {
            var aiScoreIndex = new FixtureCandidateIndex<AiScoreMatchScore>(
                consolidatedAiScores,
                score => score.HomeTeam,
                score => score.AwayTeam,
                score => score.League,
                score => score.MatchTime);
            var incompleteFixtureIndex = new FixtureCandidateIndex<SettlementFixtureGroup>(
                incompleteFixtures,
                fixture => fixture.HomeTeam,
                fixture => fixture.AwayTeam,
                fixture => fixture.League,
                fixture => fixture.ScheduledMatchTimeUtc);

            _logger.LogInformation(
                "Attempting fallback score match from AiScore for {FixtureCount} incomplete fixtures using {CandidateCount} consolidated rows ({RawCount} raw rows).",
                incompleteFixtures.Count,
                consolidatedAiScores.Count,
                aiScores.Count);

            var aiMatchedFixtures = 0;
            for (var index = 0; index < incompleteFixtures.Count; index++)
            {
                var fixture = incompleteFixtures[index];
                var aiMatch = FindBestFixtureCandidate(
                    aiScoreIndex,
                    fixture.HomeTeam,
                    fixture.AwayTeam,
                    fixture.League,
                    fixture.Date,
                    fixture.ScheduledMatchTimeUtc,
                    score => score.HomeTeam,
                    score => score.AwayTeam,
                    score => score.League,
                    score => score.MatchTime,
                    score => score.IsLive,
                    score => GetSourceQualityReliability(sourceQualityLookup, "AiScore", score.League, score.MatchTime));

                if (aiMatch != null &&
                    IsReciprocalFixtureMatch(
                        incompleteFixtureIndex,
                        fixture,
                        aiMatch,
                        score => score.HomeTeam,
                        score => score.AwayTeam,
                        score => score.League,
                        score => score.MatchTime,
                        score => score.IsLive))
                {
                    ApplyFixtureSettlement(fixture, aiMatch.Score, aiMatch.BTTSLabel, aiMatch.IsLive);
                    aiMatchedFixtures++;
                }

                LogFixtureMatchingProgress("AiScore", index + 1, incompleteFixtures.Count, aiMatchedFixtures);
            }
        }

        incompleteFixtures = eligibleSettlementFixtures
            .Where(NeedsFixtureSettlementRepair)
            .ToList();

        if (incompleteFixtures.Count > 0)
        {
            var sofaScoreRequests = incompleteFixtures
                .Select(BuildSofaScoreFixtureRequest)
                .ToList();
            var sofaScores = await _webScraperService.ScrapeSofaScoreMatchScoresAsync(sofaScoreRequests);
            await SaveSofaScoreMatchScores(sofaScores);

            if (sofaScores.Count > 0)
            {
                var sofaScoreIndex = new FixtureCandidateIndex<SofaScoreMatchScore>(
                    sofaScores,
                    score => score.HomeTeam,
                    score => score.AwayTeam,
                    score => score.League,
                    score => score.MatchTime);
                var incompleteFixtureIndex = new FixtureCandidateIndex<SettlementFixtureGroup>(
                    incompleteFixtures,
                    fixture => fixture.HomeTeam,
                    fixture => fixture.AwayTeam,
                    fixture => fixture.League,
                    fixture => fixture.ScheduledMatchTimeUtc);

                _logger.LogInformation(
                    "Attempting targeted fallback score match from SofaScore for {FixtureCount} incomplete fixtures using {CandidateCount} targeted row(s).",
                    incompleteFixtures.Count,
                    sofaScores.Count);

                var sofaMatchedFixtures = 0;
                for (var index = 0; index < incompleteFixtures.Count; index++)
                {
                    var fixture = incompleteFixtures[index];
                    var sofaMatch = FindBestFixtureCandidate(
                        sofaScoreIndex,
                        fixture.HomeTeam,
                        fixture.AwayTeam,
                        fixture.League,
                        fixture.Date,
                        fixture.ScheduledMatchTimeUtc,
                        score => score.HomeTeam,
                        score => score.AwayTeam,
                        score => score.League,
                        score => score.MatchTime,
                        score => score.IsLive,
                        score => GetSourceQualityReliability(sourceQualityLookup, "SofaScore", score.League, score.MatchTime));

                    if (sofaMatch != null &&
                        IsReciprocalFixtureMatch(
                            incompleteFixtureIndex,
                            fixture,
                            sofaMatch,
                            score => score.HomeTeam,
                            score => score.AwayTeam,
                            score => score.League,
                            score => score.MatchTime,
                            score => score.IsLive))
                    {
                        ApplyFixtureSettlement(fixture, sofaMatch.Score, sofaMatch.BTTSLabel, sofaMatch.IsLive);
                        sofaMatchedFixtures++;
                    }

                    LogFixtureMatchingProgress("SofaScore", index + 1, incompleteFixtures.Count, sofaMatchedFixtures);
                }
            }
        }

        ApplyExactFinishedSourceRepairs(eligibleSettlementFixtures, consolidatedFlashScores, consolidatedAiScores, sourceQualityLookup);
        ApplyExactLiveSourceReopens(eligibleSettlementFixtures, consolidatedFlashScores, consolidatedAiScores, sourceQualityLookup);

        // ── Matching Statistics & Diagnostics ──
        var matchedCount = eligiblePredictionsForSettlement.Count(p => !string.IsNullOrEmpty(p.ActualScore));
        var unmatchedPredictions = eligiblePredictionsForSettlement
            .Where(p => string.IsNullOrEmpty(p.ActualScore))
            .ToList();

        _logger.LogInformation(
            "📊 Score matching summary: {Matched}/{Total} predictions matched ({Percentage}%) in the {LookbackDays}-day settlement window, {Unmatched} unmatched.",
            matchedCount,
            eligiblePredictionsForSettlement.Count,
            eligiblePredictionsForSettlement.Count > 0 ? (matchedCount * 100 / eligiblePredictionsForSettlement.Count) : 0,
            lookbackDays,
            unmatchedPredictions.Count);

        if (unmatchedPredictions.Count > 0)
        {
            var topUnmatched = unmatchedPredictions.Take(15);
            foreach (var p in topUnmatched)
            {
                _logger.LogWarning(
                    "⚠️ Unmatched prediction: [{Category}] {Home} vs {Away} ({League}, {Time})",
                    p.PredictionCategory, p.HomeTeam, p.AwayTeam, p.League, p.Time);
            }

            if (unmatchedPredictions.Count > 15)
            {
                _logger.LogWarning("⚠️ ...and {More} more unmatched predictions.",
                    unmatchedPredictions.Count - 15);
            }
        }

        _logger.LogInformation(
            "✅ Predictions updated successfully for the {RunLabel} window.",
            runLabel);

        await _dbContext.SaveChangesAsync();
    }

    private static List<SettlementFixtureGroup> BuildSettlementFixtureGroups(
        IEnumerable<Prediction> predictions,
        IEnumerable<ForecastObservation> forecasts)
    {
        var fixtures = new Dictionary<(DateOnly Date, string FixtureKey, long MatchTimeTicks), SettlementFixtureGroup>();

        foreach (var prediction in predictions)
        {
            var localDate = prediction.MatchLocalDate != default
                ? prediction.MatchLocalDate
                : DateTimeProvider.ParseLocalDateOrNull(prediction.Date) ?? DateOnly.MinValue;
            var scheduledMatchTime = ResolveScheduledMatchTime(localDate, prediction.MatchLocalTime, prediction.MatchDateTime);
            var canonicalFixtureKey = string.IsNullOrWhiteSpace(prediction.FixtureKey)
                ? FixtureIdentityFactory.FromPrediction(prediction).FixtureKey
                : prediction.FixtureKey;
            var groupKey = (
                localDate,
                canonicalFixtureKey,
                scheduledMatchTime?.Ticks ?? 0L);

            if (!fixtures.TryGetValue(groupKey, out var fixture))
            {
                fixture = new SettlementFixtureGroup
                {
                    MatchLocalDate = localDate,
                    Date = localDate == DateOnly.MinValue ? prediction.Date ?? string.Empty : DateTimeProvider.FormatLocalDate(localDate),
                    HomeTeam = prediction.HomeTeam ?? string.Empty,
                    AwayTeam = prediction.AwayTeam ?? string.Empty,
                    League = prediction.League ?? string.Empty,
                    FixtureKey = groupKey.Item2,
                    ScheduledMatchTimeUtc = scheduledMatchTime
                };
                fixtures[groupKey] = fixture;
            }

            fixture.Predictions.Add(prediction);
        }

        foreach (var forecast in forecasts)
        {
            var localDate = forecast.MatchLocalDate != default
                ? forecast.MatchLocalDate
                : DateTimeProvider.ParseLocalDateOrNull(forecast.Date) ?? DateOnly.MinValue;
            var scheduledMatchTime = ResolveScheduledMatchTime(localDate, forecast.MatchLocalTime, forecast.MatchDateTime);
            var forecastFixtureKey = string.IsNullOrWhiteSpace(forecast.FixtureKey)
                ? FixtureIdentityFactory.FromForecast(forecast).FixtureKey
                : forecast.FixtureKey;
            var fixtureKey = (
                localDate,
                forecastFixtureKey,
                scheduledMatchTime?.Ticks ?? 0L);

            if (!fixtures.TryGetValue(fixtureKey, out var fixture))
            {
                fixture = new SettlementFixtureGroup
                {
                    MatchLocalDate = localDate,
                    Date = localDate == DateOnly.MinValue ? forecast.Date ?? string.Empty : DateTimeProvider.FormatLocalDate(localDate),
                    HomeTeam = forecast.HomeTeam ?? string.Empty,
                    AwayTeam = forecast.AwayTeam ?? string.Empty,
                    League = forecast.League ?? string.Empty,
                    FixtureKey = fixtureKey.Item2,
                    ScheduledMatchTimeUtc = scheduledMatchTime
                };
                fixtures[fixtureKey] = fixture;
            }

            fixture.Forecasts.Add(forecast);
        }

        return fixtures.Values
            .OrderBy(fixture => fixture.ScheduledMatchTimeUtc)
            .ThenBy(fixture => fixture.HomeTeam)
            .ThenBy(fixture => fixture.AwayTeam)
            .ToList();
    }

    private void ApplyFixtureSettlement(SettlementFixtureGroup fixture, string score, bool bttsLabel, bool isLive)
    {
        foreach (var prediction in fixture.Predictions)
        {
            UpdatePredictionSettlementState(prediction, score, bttsLabel, isLive);
        }

        foreach (var forecast in fixture.Forecasts)
        {
            UpdateForecastObservationState(forecast, score, bttsLabel, isLive);
        }
    }

    private static bool IsFixtureEligibleForSettlement(SettlementFixtureGroup fixture, DateOnly today, DateTime nowUtc)
    {
        if (fixture.ScheduledMatchTimeUtc.HasValue)
        {
            return fixture.ScheduledMatchTimeUtc.Value <= nowUtc + FutureFixtureSettlementTolerance;
        }

        if (fixture.MatchLocalDate != default)
        {
            return fixture.MatchLocalDate < today;
        }

        var parsedDate = DateTimeProvider.ParseLocalDateOrNull(fixture.Date);
        return parsedDate.HasValue && parsedDate.Value < today;
    }

    private static void ClearFutureFixtureSettlement(SettlementFixtureGroup fixture)
    {
        foreach (var prediction in fixture.Predictions)
        {
            prediction.ActualScore = null;
            prediction.ActualOutcome = null;
            prediction.IsLive = false;
        }

        foreach (var forecast in fixture.Forecasts)
        {
            forecast.ActualScore = null;
            forecast.ActualOutcome = null;
            forecast.OutcomeOccurred = null;
            forecast.IsSettled = false;
            forecast.IsLive = false;
            forecast.SettledAt = null;
        }
    }

    private static bool NeedsFixtureSettlementRepair(SettlementFixtureGroup fixture)
    {
        return fixture.Predictions.Any(NeedsPredictionSettlementRepair) ||
               fixture.Forecasts.Any(NeedsForecastSettlementRepair);
    }

    private static bool NeedsForecastSettlementRepair(ForecastObservation forecast)
    {
        return string.IsNullOrWhiteSpace(forecast.ActualScore) || forecast.IsLive || !forecast.IsSettled;
    }

    private static SofaScoreFixtureRequest BuildSofaScoreFixtureRequest(SettlementFixtureGroup fixture)
    {
        return new SofaScoreFixtureRequest
        {
            League = fixture.League,
            HomeTeam = fixture.HomeTeam,
            AwayTeam = fixture.AwayTeam,
            MatchLocalDate = fixture.MatchLocalDate,
            ScheduledMatchTimeUtc = fixture.ScheduledMatchTimeUtc,
            FixtureKey = fixture.FixtureKey
        };
    }

    private void ApplyExactFinishedSourceRepairs(
        IEnumerable<SettlementFixtureGroup> fixtures,
        IReadOnlyList<MatchScore> flashScores,
        IReadOnlyList<AiScoreMatchScore> aiScores,
        IReadOnlyDictionary<(string SourceName, string LeagueKey, string TimeBucketKey), SourceQualityProfile> sourceQualityLookup)
    {
        var flashIndex = BuildExactFinishedCandidateIndex(
            flashScores.Where(score => !score.IsLive),
            score => score.HomeTeam,
            score => score.AwayTeam,
            score => score.MatchTime,
            score => score.League);

        var aiIndex = BuildExactFinishedCandidateIndex(
            aiScores.Where(score => !score.IsLive),
            score => score.HomeTeam,
            score => score.AwayTeam,
            score => score.MatchTime,
            score => score.League);

        foreach (var fixture in fixtures)
        {
            var flashResolved = FindExactFinishedSourceCandidate(
                fixture,
                flashIndex,
                score => score.MatchTime,
                score => score.League,
                score => score.Score,
                score => GetSourceQualityReliability(sourceQualityLookup, "FlashScore", score.League, score.MatchTime));

            var aiResolved = FindExactFinishedSourceCandidate(
                fixture,
                aiIndex,
                score => score.MatchTime,
                score => score.League,
                score => score.Score,
                score => GetSourceQualityReliability(sourceQualityLookup, "AiScore", score.League, score.MatchTime));

            object? resolved = ChooseBestExactSourceCandidate(
                fixture,
                flashResolved,
                aiResolved,
                sourceQualityLookup);

            if (resolved is null)
            {
                continue;
            }

            var score = resolved switch
            {
                MatchScore flashScore => flashScore.Score,
                AiScoreMatchScore aiScore => aiScore.Score,
                _ => string.Empty
            };

            var bttsLabel = resolved switch
            {
                MatchScore flashScore => flashScore.BTTSLabel,
                AiScoreMatchScore aiScore => aiScore.BTTSLabel,
                _ => false
            };

            if (string.IsNullOrWhiteSpace(score))
            {
                continue;
            }

            ApplyFixtureSettlement(fixture, score, bttsLabel, false);
        }
    }

    private void ApplyExactLiveSourceReopens(
        IEnumerable<SettlementFixtureGroup> fixtures,
        IReadOnlyList<MatchScore> flashScores,
        IReadOnlyList<AiScoreMatchScore> aiScores,
        IReadOnlyDictionary<(string SourceName, string LeagueKey, string TimeBucketKey), SourceQualityProfile> sourceQualityLookup)
    {
        var flashFinishedIndex = BuildExactFinishedCandidateIndex(
            flashScores.Where(score => !score.IsLive),
            score => score.HomeTeam,
            score => score.AwayTeam,
            score => score.MatchTime,
            score => score.League);
        var aiFinishedIndex = BuildExactFinishedCandidateIndex(
            aiScores.Where(score => !score.IsLive),
            score => score.HomeTeam,
            score => score.AwayTeam,
            score => score.MatchTime,
            score => score.League);
        var flashLiveIndex = BuildExactFinishedCandidateIndex(
            flashScores.Where(score => score.IsLive),
            score => score.HomeTeam,
            score => score.AwayTeam,
            score => score.MatchTime,
            score => score.League);
        var aiLiveIndex = BuildExactFinishedCandidateIndex(
            aiScores.Where(score => score.IsLive),
            score => score.HomeTeam,
            score => score.AwayTeam,
            score => score.MatchTime,
            score => score.League);

        foreach (var fixture in fixtures)
        {
            var hasFinishedSource =
                FindExactFinishedSourceCandidate(
                    fixture,
                    flashFinishedIndex,
                    score => score.MatchTime,
                    score => score.League,
                    score => score.Score,
                    score => GetSourceQualityReliability(sourceQualityLookup, "FlashScore", score.League, score.MatchTime)) is not null ||
                FindExactFinishedSourceCandidate(
                    fixture,
                    aiFinishedIndex,
                    score => score.MatchTime,
                    score => score.League,
                    score => score.Score,
                    score => GetSourceQualityReliability(sourceQualityLookup, "AiScore", score.League, score.MatchTime)) is not null;

            if (hasFinishedSource)
            {
                continue;
            }

            var flashResolved = FindLatestExactLiveSourceCandidate(
                fixture,
                flashLiveIndex,
                score => score.MatchTime,
                score => GetSourceQualityReliability(sourceQualityLookup, "FlashScore", score.League, score.MatchTime));

            var aiResolved = FindLatestExactLiveSourceCandidate(
                fixture,
                aiLiveIndex,
                score => score.MatchTime,
                score => GetSourceQualityReliability(sourceQualityLookup, "AiScore", score.League, score.MatchTime));

            object? resolved = ChooseBestLiveSourceCandidate(
                flashResolved,
                aiResolved,
                sourceQualityLookup);

            if (resolved is null)
            {
                continue;
            }

            var score = resolved switch
            {
                MatchScore flashScore => flashScore.Score,
                AiScoreMatchScore aiScore => aiScore.Score,
                _ => string.Empty
            };

            var bttsLabel = resolved switch
            {
                MatchScore flashScore => flashScore.BTTSLabel,
                AiScoreMatchScore aiScore => aiScore.BTTSLabel,
                _ => false
            };

            if (string.IsNullOrWhiteSpace(score))
            {
                continue;
            }

            ApplyFixtureSettlement(fixture, score, bttsLabel, true);
        }
    }

    private void LogFixtureMatchingProgress(string sourceName, int processed, int total, int matchedFixtures)
    {
        if (total < 250)
        {
            return;
        }

        if (processed % 250 != 0 && processed != total)
        {
            return;
        }

        _logger.LogInformation(
            "{SourceName} score matching progress: {Processed}/{Total} fixtures processed, {Matched} matched so far.",
            sourceName,
            processed,
            total,
            matchedFixtures);
    }

    private static bool IsReciprocalFixtureMatch<TCandidate>(
        FixtureCandidateIndex<SettlementFixtureGroup> fixtureIndex,
        SettlementFixtureGroup expectedFixture,
        TCandidate candidate,
        Func<TCandidate, string> homeSelector,
        Func<TCandidate, string> awaySelector,
        Func<TCandidate, string?> leagueSelector,
        Func<TCandidate, DateTime?> matchTimeSelector,
        Func<TCandidate, bool> isLiveSelector)
        where TCandidate : class
    {
        var candidateDate = matchTimeSelector(candidate).HasValue
            ? DateTimeProvider.ConvertUtcToLocal(matchTimeSelector(candidate)!.Value).ToString("dd-MM-yyyy")
            : expectedFixture.Date;

        var resolvedFixture = FindBestFixtureCandidate(
            fixtureIndex,
            homeSelector(candidate),
            awaySelector(candidate),
            leagueSelector(candidate),
            candidateDate,
            matchTimeSelector(candidate),
            fixture => fixture.HomeTeam,
            fixture => fixture.AwayTeam,
            fixture => fixture.League,
            fixture => fixture.ScheduledMatchTimeUtc,
            _ => false);

        return ReferenceEquals(resolvedFixture, expectedFixture);
    }

    private static Dictionary<(string Date, string HomeKey, string AwayKey), List<T>> BuildExactFinishedCandidateIndex<T>(
        IEnumerable<T> candidates,
        Func<T, string> homeSelector,
        Func<T, string> awaySelector,
        Func<T, DateTime?> matchTimeSelector,
        Func<T, string?> leagueSelector)
        where T : class
    {
        return candidates
            .Where(candidate => matchTimeSelector(candidate).HasValue)
            .GroupBy(candidate =>
            {
                var matchTime = matchTimeSelector(candidate)!.Value;
                var date = DateTimeProvider.ConvertUtcToLocal(matchTime).ToString("dd-MM-yyyy");
                return (
                    Date: date,
                    HomeKey: ScoreMatchingHelper.CreateTeamLookupKey(homeSelector(candidate)),
                    AwayKey: ScoreMatchingHelper.CreateTeamLookupKey(awaySelector(candidate)));
            })
            .ToDictionary(group => group.Key, group => group.ToList());
    }

    private static T? FindExactFinishedSourceCandidate<T>(
        SettlementFixtureGroup fixture,
        IReadOnlyDictionary<(string Date, string HomeKey, string AwayKey), List<T>> candidateIndex,
        Func<T, DateTime?> matchTimeSelector,
        Func<T, string?> leagueSelector,
        Func<T, string?> scoreSelector,
        Func<T, double>? qualityScoreSelector = null)
        where T : class
    {
        var key = (
            Date: fixture.Date,
            HomeKey: ScoreMatchingHelper.CreateTeamLookupKey(fixture.HomeTeam),
            AwayKey: ScoreMatchingHelper.CreateTeamLookupKey(fixture.AwayTeam));

        if (!candidateIndex.TryGetValue(key, out var candidates) || candidates.Count == 0)
        {
            return default;
        }

        if (fixture.ScheduledMatchTimeUtc is null)
        {
            return ResolveExtendedExactFinishedCandidate(
                candidates
                    .Select(candidate => new RankedExactFinishedCandidate<T>(
                        candidate,
                        matchTimeSelector(candidate),
                        0d,
                        ScoreMatchingHelper.GetLeagueMatchScore(fixture.League, leagueSelector(candidate)),
                        scoreSelector(candidate),
                        qualityScoreSelector?.Invoke(candidate) ?? 0.5))
                    .OrderByDescending(candidate => candidate.MatchTime ?? DateTime.MinValue)
                    .ToList());
        }

        var ranked = candidates
            .Select(candidate => new RankedExactFinishedCandidate<T>(
                candidate,
                matchTimeSelector(candidate),
                matchTimeSelector(candidate).HasValue
                    ? Math.Abs((matchTimeSelector(candidate)!.Value - fixture.ScheduledMatchTimeUtc.Value).TotalMinutes)
                    : double.MaxValue,
                ScoreMatchingHelper.GetLeagueMatchScore(fixture.League, leagueSelector(candidate)),
                scoreSelector(candidate),
                qualityScoreSelector?.Invoke(candidate) ?? 0.5))
            .OrderBy(candidate => candidate.MinutesApart)
            .ThenByDescending(candidate => candidate.QualityScore)
            .ThenByDescending(candidate => candidate.LeagueScore)
            .ThenByDescending(candidate => candidate.MatchTime ?? DateTime.MinValue)
            .ToList();

        return ResolveExtendedExactFinishedCandidate(ranked);
    }

    private static T? FindLatestExactLiveSourceCandidate<T>(
        SettlementFixtureGroup fixture,
        IReadOnlyDictionary<(string Date, string HomeKey, string AwayKey), List<T>> candidateIndex,
        Func<T, DateTime?> matchTimeSelector,
        Func<T, double>? qualityScoreSelector = null)
        where T : class
    {
        var key = (
            Date: fixture.Date,
            HomeKey: ScoreMatchingHelper.CreateTeamLookupKey(fixture.HomeTeam),
            AwayKey: ScoreMatchingHelper.CreateTeamLookupKey(fixture.AwayTeam));

        if (!candidateIndex.TryGetValue(key, out var candidates) || candidates.Count == 0)
        {
            return default;
        }

        return candidates
            .OrderByDescending(candidate => qualityScoreSelector?.Invoke(candidate) ?? 0.5)
            .ThenByDescending(candidate => matchTimeSelector(candidate) ?? DateTime.MinValue)
            .FirstOrDefault();
    }
    
    private static bool TryParseScore(string score, out int home, out int away)
    {
        home = away = 0;
        if (string.IsNullOrWhiteSpace(score)) return false;

        // supports "1:0", "1 - 0", "1–0", "1—0", and spaces
        var normalized = score.Replace("–", "-").Replace("—", "-").Trim();

        var parts = normalized.Contains(':')
            ? normalized.Split(':', StringSplitOptions.TrimEntries)
            : normalized.Split('-', StringSplitOptions.TrimEntries);

        if (parts.Length != 2) return false;

        return int.TryParse(parts[0].Trim(), out home) && int.TryParse(parts[1].Trim(), out away);
    }

    private string DetermineDrawOutcome(string score)
    {
        return TryParseScore(score, out var h, out var a)
            ? h == a ? "Draw" : "Not Draw"
            : "Unknown";
    }

    private string DetermineOver25Outcome(string score)
    {
        return TryParseScore(score, out var h, out var a)
            ? h + a > 2 ? "Over 2.5" : "Under 2.5"
            : "Unknown";
    }

    private string DetermineStraightWinOutcome(string score)
    {
        if (!TryParseScore(score, out var h, out var a)) return "Unknown";
        if (h > a) return "Home Win";
        return h < a ? "Away Win" : "Draw";
    }

    private void UpdatePredictionSettlementState(Prediction prediction, string score, bool bttsLabel, bool isLive)
    {
        var effectiveIsLive = DetermineEffectivePredictionIsLive(prediction, score, bttsLabel, isLive);

        prediction.ActualScore = score;
        prediction.IsLive = effectiveIsLive;

        if (effectiveIsLive)
        {
            prediction.ActualOutcome = null;
            return;
        }

        prediction.ActualOutcome = DeterminePredictionActualOutcome(
            prediction.PredictionCategory,
            score,
            bttsLabel);
    }

    private void RepairPredictionOutcomeFromStoredScore(Prediction prediction)
    {
        if (prediction.IsLive || string.IsNullOrWhiteSpace(prediction.ActualScore) ||
            !IsOutcomeMissing(prediction.ActualOutcome))
        {
            return;
        }

        prediction.ActualOutcome = DeterminePredictionActualOutcome(
            prediction.PredictionCategory,
            prediction.ActualScore,
            null);
    }

    private static bool NeedsPredictionSettlementRepair(Prediction prediction)
    {
        return string.IsNullOrWhiteSpace(prediction.ActualScore) ||
               DetermineEffectiveIsLive(prediction.IsLive) ||
               (!string.IsNullOrWhiteSpace(prediction.ActualScore) && IsOutcomeMissing(prediction.ActualOutcome));
    }

    private string? DeterminePredictionActualOutcome(string predictionCategory, string score, bool? bttsLabel)
    {
        return predictionCategory switch
        {
            "BothTeamsScore" => DetermineBttsOutcome(score, bttsLabel),
            "Draw" => DetermineDrawOutcome(score),
            "Over2.5Goals" => DetermineOver25Outcome(score),
            "Under2.5Goals" => DetermineOver25Outcome(score),
            "StraightWin" => DetermineStraightWinOutcome(score),
            _ => null
        };
    }

    private string DetermineBttsOutcome(string score, bool? fallbackBttsLabel)
    {
        if (TryParseScore(score, out var homeGoals, out var awayGoals))
        {
            return homeGoals > 0 && awayGoals > 0 ? "BTTS" : "No BTTS";
        }

        return fallbackBttsLabel switch
        {
            true => "BTTS",
            false => "No BTTS",
            null => "Unknown"
        };
    }

    private static bool IsOutcomeMissing(string? actualOutcome)
    {
        return string.IsNullOrWhiteSpace(actualOutcome) ||
               string.Equals(actualOutcome, "Unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static bool DetermineEffectiveIsLive(bool sourceIsLive)
    {
        return sourceIsLive;
    }

    private void UpdateForecastObservationState(ForecastObservation forecast, string score, bool bttsLabel, bool isLive)
    {
        var effectiveIsLive = DetermineEffectiveForecastIsLive(forecast, score, bttsLabel, isLive);

        forecast.ActualScore = score;
        forecast.IsLive = effectiveIsLive;

        if (effectiveIsLive)
        {
            forecast.IsSettled = false;
            forecast.OutcomeOccurred = null;
            forecast.ActualOutcome = null;
            forecast.SettledAt = null;
            return;
        }

        forecast.IsSettled = true;
        forecast.SettledAt = DateTime.UtcNow;
        forecast.OutcomeOccurred = DetermineForecastOutcomeOccurred(forecast.Market, score, bttsLabel);
        forecast.ActualOutcome = DetermineForecastActualOutcome(forecast.Market, score, bttsLabel);
    }

    private bool? DetermineForecastOutcomeOccurred(PredictionMarket market, string score, bool bttsLabel)
    {
        switch (market)
        {
            case PredictionMarket.BothTeamsScore:
                if (TryParseScore(score, out var homeGoals, out var awayGoals))
                    return homeGoals > 0 && awayGoals > 0;

                return bttsLabel;

            case PredictionMarket.Over25Goals:
                return TryParseScore(score, out var homeOver, out var awayOver)
                    ? homeOver + awayOver > 2
                    : null;

            case PredictionMarket.Under25Goals:
                return TryParseScore(score, out var homeUnder, out var awayUnder)
                    ? homeUnder + awayUnder <= 2
                    : null;

            case PredictionMarket.Draw:
                return TryParseScore(score, out var homeDraw, out var awayDraw)
                    ? homeDraw == awayDraw
                    : null;

            case PredictionMarket.HomeWin:
                return TryParseScore(score, out var homeWin, out var awayWin)
                    ? homeWin > awayWin
                    : null;

            case PredictionMarket.AwayWin:
                return TryParseScore(score, out var homeAway, out var awayAway)
                    ? awayAway > homeAway
                    : null;

            case PredictionMarket.StraightWin:
                return DetermineStraightWinOutcome(score) == "Home Win" || DetermineStraightWinOutcome(score) == "Away Win";

            default:
                return null;
        }
    }

    private string? DetermineForecastActualOutcome(PredictionMarket market, string score, bool bttsLabel)
    {
        return market switch
        {
            PredictionMarket.BothTeamsScore => (DetermineForecastOutcomeOccurred(market, score, bttsLabel) ?? false) ? "BTTS" : "No BTTS",
            PredictionMarket.Over25Goals => DetermineOver25Outcome(score),
            PredictionMarket.Under25Goals => DetermineOver25Outcome(score),
            PredictionMarket.Draw => DetermineDrawOutcome(score),
            PredictionMarket.HomeWin => (DetermineForecastOutcomeOccurred(market, score, bttsLabel) ?? false) ? "Home Win" : "Not Home Win",
            PredictionMarket.AwayWin => (DetermineForecastOutcomeOccurred(market, score, bttsLabel) ?? false) ? "Away Win" : "Not Away Win",
            PredictionMarket.StraightWin => DetermineStraightWinOutcome(score),
            _ => null
        };
    }

    private bool DetermineEffectivePredictionIsLive(Prediction prediction, string score, bool? bttsLabel, bool sourceIsLive)
    {
        if (!sourceIsLive)
        {
            return false;
        }

        return PredictionMarketExtensions.TryFromCategory(prediction.PredictionCategory, out var market) &&
               CanSettleMarketEarly(market, score, bttsLabel)
            ? false
            : DetermineEffectiveIsLive(sourceIsLive);
    }

    private bool DetermineEffectiveForecastIsLive(ForecastObservation forecast, string score, bool? bttsLabel, bool sourceIsLive)
    {
        if (!sourceIsLive)
        {
            return false;
        }

        return CanSettleMarketEarly(forecast.Market, score, bttsLabel)
            ? false
            : DetermineEffectiveIsLive(sourceIsLive);
    }

    private static bool CanSettleMarketEarly(PredictionMarket market, string score, bool? bttsLabel)
    {
        return market switch
        {
            PredictionMarket.BothTeamsScore => HasBothTeamsScored(score, bttsLabel),
            PredictionMarket.Over25Goals => HasOver25BeenMet(score),
            _ => false
        };
    }

    private static bool HasBothTeamsScored(string score, bool? fallbackBttsLabel)
    {
        if (TryParseScore(score, out var homeGoals, out var awayGoals))
        {
            return homeGoals > 0 && awayGoals > 0;
        }

        return fallbackBttsLabel == true;
    }

    private static bool HasOver25BeenMet(string score)
    {
        return TryParseScore(score, out var homeGoals, out var awayGoals) &&
               homeGoals + awayGoals > 2;
    }

    
    private async Task RebuildCalibrationProfiles()
    {
        _logger.LogInformation("Starting market calibration rebuild...");
        await _calibrationService.RebuildProfilesAsync();
        var profileCount = await _dbContext.MarketCalibrationProfiles.CountAsync();
        _logger.LogInformation("Updated {Count} calibration profiles.", profileCount);
    }

    private async Task RebuildThresholdProfiles()
    {
        _logger.LogInformation("Starting threshold tuning rebuild...");
        await _thresholdTuningService.RebuildProfilesAsync();
        var profileCount = await _dbContext.ThresholdProfiles.CountAsync();
        _logger.LogInformation("Updated {Count} threshold profiles.", profileCount);
    }
    
    private async Task SaveMatchScores(List<MatchScore> scores)
    {
        if (scores.Count == 0) return;

        var localDates = scores
            .Select(score => DateTimeProvider.ConvertUtcToLocal(score.MatchTime).Date)
            .Distinct()
            .ToList();
        var windowStartUtc = DateTimeProvider.ConvertLocalToUtc(localDates.Min());
        var windowEndUtc = DateTimeProvider.ConvertLocalToUtc(localDates.Max().AddDays(1));

        var existingScoresList = await _dbContext.MatchScores
            .Where(s => s.MatchTime >= windowStartUtc && s.MatchTime < windowEndUtc)
            .ToListAsync();

        var existingScoresDict = existingScoresList
            .GroupBy(GetStoredScoreSnapshotKey)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var incomingScore in scores)
        {
            var key = GetStoredScoreSnapshotKey(incomingScore);

            if (existingScoresDict.TryGetValue(key, out var existingRecord))
            {
                existingRecord.MatchTime = ResolvePreferredStoredMatchTime(existingRecord.MatchTime, incomingScore.MatchTime, existingRecord.IsLive, incomingScore.IsLive);

                if (ShouldOverwriteStoredScore(existingRecord.Score, existingRecord.BTTSLabel, existingRecord.IsLive, existingRecord.MatchTime, incomingScore))
                {
                    existingRecord.Score = incomingScore.Score;
                    existingRecord.IsLive = incomingScore.IsLive;
                    existingRecord.BTTSLabel = incomingScore.BTTSLabel;
                }
            }
            else
            {
                _dbContext.MatchScores.Add(incomingScore);
                existingScoresDict[key] = incomingScore;
            }
        }

        await _dbContext.SaveChangesAsync();
    }

    private async Task SaveAiScoreMatchScores(List<AiScoreMatchScore> scores)
    {
        if (scores.Count == 0) return;

        var minTime = scores.Min(s => s.MatchTime);
        var maxTime = scores.Max(s => s.MatchTime);

        var existingScoresList = await _dbContext.AiScoreMatchScores
            .Where(s => s.MatchTime >= minTime && s.MatchTime <= maxTime)
            .ToListAsync();

        var existingScoresDict = existingScoresList
            .GroupBy(s => (s.HomeTeam, s.AwayTeam, s.MatchTime))
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var incomingScore in scores)
        {
            var key = (incomingScore.HomeTeam, incomingScore.AwayTeam, incomingScore.MatchTime);

            if (existingScoresDict.TryGetValue(key, out var existingRecord))
            {
                if (ShouldOverwriteStoredScore(existingRecord.Score, existingRecord.BTTSLabel, existingRecord.IsLive, existingRecord.MatchTime, incomingScore))
                {
                    existingRecord.Score = incomingScore.Score;
                    existingRecord.IsLive = incomingScore.IsLive;
                    existingRecord.BTTSLabel = incomingScore.BTTSLabel;
                }
            }
            else
            {
                _dbContext.AiScoreMatchScores.Add(incomingScore);
            }
        }

        await _dbContext.SaveChangesAsync();
    }

    
    [AutomaticRetry(OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public async Task CleanupOldPredictionsAndMatchDataAsync()
    {
        var cutoffDate = DateTimeProvider.GetLocalTime().AddDays(-90).Date;
    
        // Delete old predictions directly in the database using CreatedAt (proper DateTime)
        
        await _dbContext.Predictions
            .Where(p => p.CreatedAt.Date < cutoffDate)
            .ExecuteDeleteAsync();

        await _dbContext.ForecastObservations
            .Where(f => f.CreatedAt.Date < cutoffDate)
            .ExecuteDeleteAsync();
    
        // MatchData stores Date as a string, so we filter in memory but only once
        var allMatchData = await _dbContext.MatchDatas.ToListAsync();
        var oldMatchData = allMatchData
            .Where(m => DateTime.TryParse(m.Date, out var d) && d.Date < cutoffDate)
            .ToList();
    
        if (oldMatchData.Count > 0)
        {
            _dbContext.MatchDatas.RemoveRange(oldMatchData);
            await _dbContext.SaveChangesAsync();
        }

        // Cleanup old AiScore match scores
        var cutoffUtc = DateTime.SpecifyKind(cutoffDate, DateTimeKind.Utc);
        await _dbContext.AiScoreMatchScores
            .Where(s => s.MatchTime < cutoffUtc)
            .ExecuteDeleteAsync();
    
        // Cleanup old MatchScores — retain 90 days for calibration and regression.
        var scoreCutoffDate = DateTimeProvider.GetLocalTime().AddDays(-90).Date;
        var scoreCutoffUtc = DateTime.SpecifyKind(scoreCutoffDate, DateTimeKind.Utc);
        await _dbContext.MatchScores
            .Where(s => s.MatchTime < scoreCutoffUtc)
            .ExecuteDeleteAsync();

        // Cleanup scraping logs older than 2 days
        var logCutoff = DateTimeProvider.GetLocalTime().AddDays(-2);
        var deletedLogs = await _dbContext.ScrapingLogs
            .Where(l => l.Timestamp < logCutoff)
            .ExecuteDeleteAsync();

        if (deletedLogs > 0)
        {
            _logger.LogInformation("🧹 Deleted {Count} scraping logs older than 2 days.", deletedLogs);
        }
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

        var predictions = await _dbContext.Predictions
            .Where(prediction => dates.Contains(prediction.MatchLocalDate))
            .ToListAsync();

        var forecasts = await _dbContext.ForecastObservations
            .Where(forecast => dates.Contains(forecast.MatchLocalDate))
            .ToListAsync();

        var matchLookup = matches
            .GroupBy(match => (
                Date: match.MatchLocalDate ?? default,
                Home: Norm(match.HomeTeam),
                Away: Norm(match.AwayTeam),
                League: Norm(match.League)))
            .ToDictionary(group => group.Key, group => group.OrderBy(m => m.MatchDateTime).First());

        var teamLookup = matches
            .GroupBy(match => (
                Home: Norm(match.HomeTeam),
                Away: Norm(match.AwayTeam),
                League: Norm(match.League)))
            .ToDictionary(group => group.Key, group => group.OrderBy(m => m.MatchDateTime).ToList());

        var updatedPredictions = 0;
        var updatedForecasts = 0;

        foreach (var prediction in predictions)
        {
            var matched = FindMatchingMatchData(prediction.MatchLocalDate, prediction.HomeTeam, prediction.AwayTeam, prediction.League, prediction.MatchDateTime, matchLookup, teamLookup);
            if (matched != null && ApplyStoredTime(prediction, matched))
            {
                updatedPredictions++;
            }
        }

        foreach (var forecast in forecasts)
        {
            var matched = FindMatchingMatchData(forecast.MatchLocalDate, forecast.HomeTeam, forecast.AwayTeam, forecast.League, forecast.MatchDateTime, matchLookup, teamLookup);
            if (matched != null && ApplyStoredTime(forecast, matched))
            {
                updatedForecasts++;
            }
        }

        if (updatedPredictions > 0 || updatedForecasts > 0)
        {
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation(
                "Repaired stored kickoff times for {PredictionCount} predictions and {ForecastCount} forecasts.",
                updatedPredictions,
                updatedForecasts);
        }
    }

    private async Task BackfillCanonicalFixtureFieldsAsync(int lookbackDays = 90)
    {
        var today = DateTimeProvider.GetLocalDate();
        var dates = Enumerable.Range(0, Math.Max(lookbackDays, 0) + 1)
            .Select(offset => today.AddDays(-offset))
            .ToHashSet();

        var matches = await _dbContext.MatchDatas
            .Where(match =>
                (match.MatchLocalDate.HasValue && dates.Contains(match.MatchLocalDate.Value)) ||
                (!match.MatchLocalDate.HasValue && match.Date != null))
            .ToListAsync();
        var predictions = await _dbContext.Predictions
            .Where(prediction => dates.Contains(prediction.MatchLocalDate) || prediction.MatchLocalDate == default)
            .ToListAsync();
        var forecasts = await _dbContext.ForecastObservations
            .Where(forecast => dates.Contains(forecast.MatchLocalDate) || forecast.MatchLocalDate == default)
            .ToListAsync();

        var updatedMatches = 0;
        foreach (var match in matches)
        {
            if (ApplyCanonicalFixtureIdentity(match))
            {
                updatedMatches++;
            }
        }

        var updatedPredictions = 0;
        foreach (var prediction in predictions)
        {
            if (ApplyCanonicalFixtureIdentity(prediction))
            {
                updatedPredictions++;
            }
        }

        var updatedForecasts = 0;
        foreach (var forecast in forecasts)
        {
            if (ApplyCanonicalFixtureIdentity(forecast))
            {
                updatedForecasts++;
            }
        }

        if (updatedMatches > 0 || updatedPredictions > 0 || updatedForecasts > 0)
        {
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation(
                "Backfilled canonical fixture fields for {MatchCount} match rows, {PredictionCount} predictions, and {ForecastCount} forecasts.",
                updatedMatches,
                updatedPredictions,
                updatedForecasts);
        }
    }

    public async Task BackfillDecisionProvenanceAsync(int lookbackDays = 90)
    {
        var today = DateTimeProvider.GetLocalDate();
        var dates = Enumerable.Range(0, Math.Max(lookbackDays, 0) + 1)
            .Select(offset => today.AddDays(-offset))
            .ToHashSet();

        var predictions = await _dbContext.Predictions
            .Where(prediction =>
                dates.Contains(prediction.MatchLocalDate) &&
                (string.IsNullOrEmpty(prediction.CalibratorUsed) ||
                 prediction.CalibratorUsed == "Unknown" ||
                 string.IsNullOrEmpty(prediction.ThresholdSource) ||
                 prediction.ThresholdSource == "Unknown" ||
                 prediction.ThresholdUsed <= 0 ||
                 !prediction.WasPublished))
            .ToListAsync();

        var forecasts = await _dbContext.ForecastObservations
            .Where(forecast =>
                dates.Contains(forecast.MatchLocalDate) &&
                (string.IsNullOrEmpty(forecast.CalibratorUsed) ||
                 forecast.CalibratorUsed == "Unknown" ||
                 string.IsNullOrEmpty(forecast.ThresholdSource) ||
                 forecast.ThresholdSource == "Unknown" ||
                 forecast.ThresholdUsed <= 0))
            .ToListAsync();

        var updatedPredictions = 0;
        foreach (var prediction in predictions)
        {
            if (!TryResolvePredictionMarket(prediction, out var market))
            {
                continue;
            }

            if (ApplyDecisionBackfill(prediction, market))
            {
                updatedPredictions++;
            }
        }

        var updatedForecasts = 0;
        foreach (var forecast in forecasts)
        {
            if (ApplyDecisionBackfill(forecast, forecast.Market))
            {
                updatedForecasts++;
            }
        }

        if (updatedPredictions > 0 || updatedForecasts > 0)
        {
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation(
                "Backfilled decision provenance for {PredictionCount} predictions and {ForecastCount} forecasts.",
                updatedPredictions,
                updatedForecasts);
        }
    }
    
    private async Task<IReadOnlyList<Prediction>> SavePredictions(
        IEnumerable<PredictionCandidate> forecastCandidates,
        IEnumerable<PredictionCandidate> candidates,
        PredictionRun predictionRun)
    {
        var forecastCandidateList = forecastCandidates.ToList();
        var candidateList = DeduplicatePublishedCandidates(candidates, predictionRun.TargetLocalDate.ToString("dd-MM-yyyy"));
        var nowUtc = DateTime.UtcNow;
        var touchedFixtureKeys = BuildCandidateFixtureKeySet(forecastCandidateList);
        var lockedFixtureKeys = BuildLockedCandidateFixtureKeySet(forecastCandidateList, nowUtc);
        if (touchedFixtureKeys.Count == 0)
        {
            touchedFixtureKeys = BuildCandidateFixtureKeySet(candidateList);
            lockedFixtureKeys = BuildLockedCandidateFixtureKeySet(candidateList, nowUtc);
        }

        if (touchedFixtureKeys.Count == 0)
        {
            return [];
        }

        var targetDate = predictionRun.TargetLocalDate;
        var currentPredictions = await _dbContext.Predictions
            .Where(p => p.MatchLocalDate == targetDate && p.IsCurrentRevision)
            .ToListAsync();
        var historicalPredictions = await _dbContext.Predictions
            .Where(p => p.MatchLocalDate == targetDate)
            .ToListAsync();

        var currentByKey = currentPredictions
            .GroupBy(GetPredictionKey)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(prediction => prediction.RevisionNumber)
                    .ThenByDescending(prediction => prediction.CreatedAt)
                    .ThenByDescending(prediction => prediction.Id)
                    .First());
        var historicalByKey = historicalPredictions
            .GroupBy(GetPredictionKey)
            .ToDictionary(group => group.Key, group => group.ToList());
        var revisionByKey = historicalPredictions
            .GroupBy(GetPredictionKey)
            .ToDictionary(group => group.Key, group => group.Max(prediction => prediction.RevisionNumber));

        RestoreLockedPredictionCurrents(candidateList, currentByKey, historicalByKey, nowUtc);

        var createdPredictions = new List<Prediction>();
        foreach (var existingRecord in currentPredictions.Where(prediction =>
                     touchedFixtureKeys.Contains(GetPredictionFixtureKey(prediction)) &&
                     !lockedFixtureKeys.Contains(GetPredictionFixtureKey(prediction))))
        {
            existingRecord.IsCurrentRevision = false;
            existingRecord.SupersededAt = nowUtc;
        }

        var skippedLockedCandidates = 0;
        foreach (var candidate in candidateList)
        {
            if (lockedFixtureKeys.Contains(GetCandidateFixtureKey(candidate)))
            {
                skippedLockedCandidates++;
                continue;
            }

            var currentKey = GetCandidatePredictionKey(candidate);
            currentByKey.TryGetValue(currentKey, out var currentRecord);
            var nextRevision = revisionByKey.TryGetValue(currentKey, out var revisionNumber)
                ? revisionNumber + 1
                : 1;

            var prediction = new Prediction
            {
                HomeTeam = candidate.HomeTeam.Trim(),
                AwayTeam = candidate.AwayTeam.Trim(),
                League = candidate.League.Trim(),
                PredictionCategory = candidate.PredictionCategory,
                PredictedOutcome = candidate.PredictedOutcome,
                RawConfidenceScore = Math.Round((decimal)candidate.RawProbability, 4),
                ConfidenceScore = Math.Round((decimal)candidate.CalibratedProbability, 4),
                CalibratorUsed = candidate.CalibratorUsed,
                ThresholdUsed = Math.Round(candidate.ThresholdUsed, 4),
                ThresholdSource = candidate.ThresholdSource,
                WasPublished = candidate.WasPublished,
                Date = candidate.Date,
                Time = candidate.Time,
                MatchLocalDate = candidate.MatchLocalDate,
                MatchLocalTime = candidate.MatchLocalTime,
                MatchDateTime = candidate.MatchDateTime,
                FixtureKey = candidate.FixtureKey,
                PredictionRunId = predictionRun.Id,
                RunLabel = predictionRun.RunLabel,
                RunReason = predictionRun.RunReason,
                RevisionNumber = nextRevision,
                IsCurrentRevision = true,
                ActualOutcome = currentRecord?.ActualOutcome,
                ActualScore = currentRecord?.ActualScore,
                IsLive = currentRecord?.IsLive ?? false
            };

            createdPredictions.Add(prediction);
            _dbContext.Predictions.Add(prediction);
        }

        if (skippedLockedCandidates > 0)
        {
            _logger.LogInformation(
                "Skipped {SkippedCount} post-kickoff published prediction candidate(s) to preserve the pre-kickoff current revision.",
                skippedLockedCandidates);
        }

        await _dbContext.SaveChangesAsync();
        return createdPredictions;
    }

    private async Task SaveForecastObservations(
        IEnumerable<PredictionCandidate> forecastCandidates,
        IEnumerable<PredictionCandidate> publishedCandidates,
        PredictionRun predictionRun)
    {
        var forecastList = DeduplicateForecastCandidates(
            forecastCandidates,
            predictionRun.TargetLocalDate.ToString("dd-MM-yyyy"));
        if (!forecastList.Any()) return;

        var nowUtc = DateTime.UtcNow;
        var publishedKeys = publishedCandidates
            .Select(GetCandidateObservationKey)
            .ToHashSet(StringComparer.Ordinal);

        var targetDate = predictionRun.TargetLocalDate;
        var currentForecasts = await _dbContext.ForecastObservations
            .Where(forecast => forecast.MatchLocalDate == targetDate && forecast.IsCurrentRevision)
            .ToListAsync();
        var historicalForecasts = await _dbContext.ForecastObservations
            .Where(forecast => forecast.MatchLocalDate == targetDate)
            .ToListAsync();

        var currentByKey = currentForecasts
            .GroupBy(GetObservationKey)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(forecast => forecast.RevisionNumber)
                    .ThenByDescending(forecast => forecast.CreatedAt)
                    .ThenByDescending(forecast => forecast.Id)
                    .First());
        var historicalByKey = historicalForecasts
            .GroupBy(GetObservationKey)
            .ToDictionary(group => group.Key, group => group.ToList());
        var revisionByKey = historicalForecasts
            .GroupBy(GetObservationKey)
            .ToDictionary(group => group.Key, group => group.Max(forecast => forecast.RevisionNumber));
        var touchedFixtureKeys = BuildCandidateFixtureKeySet(forecastList);
        var lockedFixtureKeys = BuildLockedCandidateFixtureKeySet(forecastList, nowUtc);

        RestoreLockedForecastCurrents(forecastList, currentByKey, historicalByKey, nowUtc);

        foreach (var existingRecord in currentForecasts.Where(forecast =>
                     touchedFixtureKeys.Contains(GetForecastFixtureKey(forecast)) &&
                     !lockedFixtureKeys.Contains(GetForecastFixtureKey(forecast))))
        {
            existingRecord.IsCurrentRevision = false;
            existingRecord.SupersededAt = nowUtc;
        }

        var skippedLockedForecasts = 0;
        foreach (var candidate in forecastList)
        {
            if (lockedFixtureKeys.Contains(GetCandidateFixtureKey(candidate)))
            {
                skippedLockedForecasts++;
                continue;
            }

            var key = GetCandidateObservationKey(candidate);
            var isPublished = publishedKeys.Contains(key);
            currentByKey.TryGetValue(key, out var currentRecord);
            var nextRevision = revisionByKey.TryGetValue(key, out var revisionNumber)
                ? revisionNumber + 1
                : 1;

            _dbContext.ForecastObservations.Add(new ForecastObservation
            {
                Date = candidate.Date,
                Time = candidate.Time,
                MatchLocalDate = candidate.MatchLocalDate,
                MatchLocalTime = candidate.MatchLocalTime,
                MatchDateTime = candidate.MatchDateTime,
                FixtureKey = candidate.FixtureKey,
                League = candidate.League,
                HomeTeam = candidate.HomeTeam,
                AwayTeam = candidate.AwayTeam,
                Market = candidate.Market,
                PredictedOutcome = candidate.PredictedOutcome,
                RawProbability = candidate.RawProbability,
                CalibratedProbability = candidate.CalibratedProbability,
                CalibratorUsed = candidate.CalibratorUsed,
                ThresholdUsed = Math.Round(candidate.ThresholdUsed, 4),
                ThresholdSource = candidate.ThresholdSource,
                IsPublished = isPublished,
                PredictionRunId = predictionRun.Id,
                RunLabel = predictionRun.RunLabel,
                RunReason = predictionRun.RunReason,
                RevisionNumber = nextRevision,
                IsCurrentRevision = true,
                ActualOutcome = currentRecord?.ActualOutcome,
                ActualScore = currentRecord?.ActualScore,
                OutcomeOccurred = currentRecord?.OutcomeOccurred,
                IsLive = currentRecord?.IsLive ?? false,
                IsSettled = currentRecord?.IsSettled ?? false,
                SettledAt = currentRecord?.SettledAt
            });
        }

        if (skippedLockedForecasts > 0)
        {
            _logger.LogInformation(
                "Skipped {SkippedCount} post-kickoff forecast candidate(s) to preserve the pre-kickoff current revision.",
                skippedLockedForecasts);
        }

        await _dbContext.SaveChangesAsync();
    }

    private async Task CapturePublishOddsSnapshotsAsync(
        IReadOnlyList<Prediction> predictions,
        IReadOnlyCollection<MatchData> matchDatas,
        IReadOnlyList<SourceMarketFixture> sourceFixtures)
    {
        if (predictions.Count == 0)
        {
            return;
        }

        var createdCount = await SavePredictionOddsSnapshotsAsync(predictions, matchDatas, sourceFixtures, PredictionOddsSnapshotKind.Publish);
        if (createdCount > 0)
        {
            _logger.LogInformation("Captured {Count} publish odds snapshot(s) for the latest prediction run.", createdCount);
        }
    }

    private async Task<int> SavePredictionOddsSnapshotsAsync(
        IReadOnlyCollection<Prediction> predictions,
        IReadOnlyCollection<MatchData> matchDatas,
        IReadOnlyList<SourceMarketFixture> sourceFixtures,
        PredictionOddsSnapshotKind snapshotKind)
    {
        if (predictions.Count == 0)
        {
            return 0;
        }

        var predictionIds = predictions.Select(prediction => prediction.Id).ToList();
        var existingKeys = await _dbContext.PredictionOddsSnapshots
            .AsNoTracking()
            .Where(snapshot =>
                predictionIds.Contains(snapshot.PredictionId) &&
                snapshot.SnapshotKind == snapshotKind)
            .Select(snapshot => new { snapshot.PredictionId, snapshot.SourceName })
            .ToListAsync();

        var existingKeySet = existingKeys
            .Select(snapshot => BuildSnapshotDuplicateKey(snapshot.PredictionId, snapshot.SourceName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var byFixtureAndLeague = matchDatas
            .GroupBy(match => BuildPredictionMatchKey(match.MatchLocalDate, match.FixtureKey, match.League, match.HomeTeam, match.AwayTeam, includeLeague: true))
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var byFixture = matchDatas
            .GroupBy(match => BuildPredictionMatchKey(match.MatchLocalDate, match.FixtureKey, null, match.HomeTeam, match.AwayTeam, includeLeague: false))
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var createdSnapshots = new List<PredictionOddsSnapshot>();
        foreach (var prediction in predictions)
        {
            var matchData = ResolveMatchDataForPrediction(prediction, byFixtureAndLeague, byFixture);
            if (matchData is null)
            {
                continue;
            }

            var sourceFixture = SourceMarketFixtureMatcher.FindBestFixture(
                sourceFixtures,
                prediction.HomeTeam,
                prediction.AwayTeam,
                prediction.League,
                prediction.MatchDateTime);

            if (!MarketQuoteResolver.TryResolve(matchData, prediction, sourceFixture, out var quote))
            {
                continue;
            }

            var duplicateKey = BuildSnapshotDuplicateKey(prediction.Id, quote.SourceName);
            if (existingKeySet.Contains(duplicateKey))
            {
                continue;
            }

            createdSnapshots.Add(new PredictionOddsSnapshot
            {
                PredictionId = prediction.Id,
                PredictionRunId = prediction.PredictionRunId,
                SourceName = quote.SourceName,
                Market = prediction.PredictionCategory,
                Outcome = prediction.PredictedOutcome,
                DecimalOdds = Math.Round(quote.DecimalOdds, 4),
                ImpliedProbability = Math.Round(quote.ImpliedProbability, 6),
                OddsDerivationSource = quote.OddsDerivationSource,
                SnapshotKind = snapshotKind,
                CapturedAtUtc = DateTime.UtcNow
            });

            existingKeySet.Add(duplicateKey);
        }

        if (createdSnapshots.Count == 0)
        {
            return 0;
        }

        _dbContext.PredictionOddsSnapshots.AddRange(createdSnapshots);
        await _dbContext.SaveChangesAsync();
        return createdSnapshots.Count;
    }

    private static string GetObservationKey(ForecastObservation forecast)
    {
        return $"{ResolveFixtureKey(forecast.FixtureKey, forecast.MatchLocalDate, forecast.League, forecast.HomeTeam, forecast.AwayTeam)}|{forecast.Market}";
    }

    private static string GetPredictionKey(Prediction prediction)
    {
        return $"{GetPredictionFixtureKey(prediction)}|{prediction.PredictionCategory}";
    }

    private static string GetPredictionFixtureKey(Prediction prediction)
    {
        return ResolveFixtureKey(
            prediction.FixtureKey,
            prediction.MatchLocalDate,
            prediction.League,
            prediction.HomeTeam,
            prediction.AwayTeam);
    }

    private static string GetForecastFixtureKey(ForecastObservation forecast)
    {
        return ResolveFixtureKey(
            forecast.FixtureKey,
            forecast.MatchLocalDate,
            forecast.League,
            forecast.HomeTeam,
            forecast.AwayTeam);
    }

    private static string GetCandidateFixtureKey(PredictionCandidate candidate)
    {
        return ResolveFixtureKey(
            candidate.FixtureKey,
            candidate.MatchLocalDate,
            candidate.League,
            candidate.HomeTeam,
            candidate.AwayTeam);
    }

    private static HashSet<string> BuildCandidateFixtureKeySet(IEnumerable<PredictionCandidate> candidates)
    {
        return candidates
            .Select(GetCandidateFixtureKey)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> BuildLockedCandidateFixtureKeySet(IEnumerable<PredictionCandidate> candidates, DateTime nowUtc)
    {
        return candidates
            .Where(candidate => IsFixturePastKickoffGrace(candidate.MatchDateTime, nowUtc))
            .Select(GetCandidateFixtureKey)
            .ToHashSet(StringComparer.Ordinal);
    }

    private void RestoreLockedPredictionCurrents(
        IEnumerable<PredictionCandidate> candidates,
        IDictionary<string, Prediction> currentByKey,
        IReadOnlyDictionary<string, List<Prediction>> historicalByKey,
        DateTime nowUtc)
    {
        foreach (var lockedKey in candidates
                     .Where(candidate => IsFixturePastKickoffGrace(candidate.MatchDateTime, nowUtc))
                     .Select(GetCandidatePredictionKey)
                     .Distinct(StringComparer.Ordinal))
        {
            if (!currentByKey.TryGetValue(lockedKey, out var currentRecord) ||
                IsEligiblePreKickoffSnapshot(currentRecord.MatchDateTime, currentRecord.CreatedAt))
            {
                continue;
            }

            if (!historicalByKey.TryGetValue(lockedKey, out var history))
            {
                continue;
            }

            var restoredRecord = history
                .Where(record => record.Id != currentRecord.Id && IsEligiblePreKickoffSnapshot(record.MatchDateTime, record.CreatedAt))
                .OrderByDescending(record => record.RevisionNumber)
                .ThenByDescending(record => record.CreatedAt)
                .ThenByDescending(record => record.Id)
                .FirstOrDefault();

            if (restoredRecord is null)
            {
                continue;
            }

            restoredRecord.IsCurrentRevision = true;
            restoredRecord.SupersededAt = null;
            restoredRecord.ActualOutcome = currentRecord.ActualOutcome;
            restoredRecord.ActualScore = currentRecord.ActualScore;
            restoredRecord.IsLive = currentRecord.IsLive;

            currentRecord.IsCurrentRevision = false;
            currentRecord.SupersededAt = nowUtc;
            currentByKey[lockedKey] = restoredRecord;
        }
    }

    private void RestoreLockedForecastCurrents(
        IEnumerable<PredictionCandidate> candidates,
        IDictionary<string, ForecastObservation> currentByKey,
        IReadOnlyDictionary<string, List<ForecastObservation>> historicalByKey,
        DateTime nowUtc)
    {
        foreach (var lockedKey in candidates
                     .Where(candidate => IsFixturePastKickoffGrace(candidate.MatchDateTime, nowUtc))
                     .Select(GetCandidateObservationKey)
                     .Distinct(StringComparer.Ordinal))
        {
            if (!currentByKey.TryGetValue(lockedKey, out var currentRecord) ||
                IsEligiblePreKickoffSnapshot(currentRecord.MatchDateTime, currentRecord.CreatedAt))
            {
                continue;
            }

            if (!historicalByKey.TryGetValue(lockedKey, out var history))
            {
                continue;
            }

            var restoredRecord = history
                .Where(record => record.Id != currentRecord.Id && IsEligiblePreKickoffSnapshot(record.MatchDateTime, record.CreatedAt))
                .OrderByDescending(record => record.RevisionNumber)
                .ThenByDescending(record => record.CreatedAt)
                .ThenByDescending(record => record.Id)
                .FirstOrDefault();

            if (restoredRecord is null)
            {
                continue;
            }

            restoredRecord.IsCurrentRevision = true;
            restoredRecord.SupersededAt = null;
            restoredRecord.ActualOutcome = currentRecord.ActualOutcome;
            restoredRecord.ActualScore = currentRecord.ActualScore;
            restoredRecord.OutcomeOccurred = currentRecord.OutcomeOccurred;
            restoredRecord.IsLive = currentRecord.IsLive;
            restoredRecord.IsSettled = currentRecord.IsSettled;
            restoredRecord.SettledAt = currentRecord.SettledAt;

            currentRecord.IsCurrentRevision = false;
            currentRecord.SupersededAt = nowUtc;
            currentByKey[lockedKey] = restoredRecord;
        }
    }

    private static bool IsFixturePastKickoffGrace(DateTime? kickoffUtc, DateTime nowUtc)
    {
        return kickoffUtc.HasValue && nowUtc > kickoffUtc.Value.Add(CurrentRevisionKickoffGrace);
    }

    private static bool IsEligiblePreKickoffSnapshot(DateTime? kickoffUtc, DateTime createdAtUtc)
    {
        return !kickoffUtc.HasValue || createdAtUtc <= kickoffUtc.Value.Add(CurrentRevisionKickoffGrace);
    }

    private static string GetCandidateObservationKey(PredictionCandidate candidate)
    {
        return $"{GetCandidateFixtureKey(candidate)}|{candidate.Market}";
    }

    private static string GetCandidatePredictionKey(PredictionCandidate candidate)
    {
        return $"{GetCandidateFixtureKey(candidate)}|{candidate.PredictionCategory}";
    }

    private static MatchData? ResolveMatchDataForPrediction(
        Prediction prediction,
        IReadOnlyDictionary<string, List<MatchData>> byFixtureAndLeague,
        IReadOnlyDictionary<string, List<MatchData>> byFixture)
    {
        var leagueKey = BuildPredictionMatchKey(prediction.MatchLocalDate, prediction.FixtureKey, prediction.League, prediction.HomeTeam, prediction.AwayTeam, includeLeague: true);
        if (byFixtureAndLeague.TryGetValue(leagueKey, out var leagueMatches))
        {
            return SelectBestSnapshotMatchData(leagueMatches, prediction);
        }

        var fixtureKey = BuildPredictionMatchKey(prediction.MatchLocalDate, prediction.FixtureKey, null, prediction.HomeTeam, prediction.AwayTeam, includeLeague: false);
        return byFixture.TryGetValue(fixtureKey, out var fallbackMatches)
            ? SelectBestSnapshotMatchData(fallbackMatches, prediction)
            : null;
    }

    private static MatchData SelectBestSnapshotMatchData(IEnumerable<MatchData> matches, Prediction prediction)
    {
        return matches
            .OrderBy(match => match.MatchDateTime.HasValue ? Math.Abs((match.MatchDateTime.Value - (prediction.MatchDateTime ?? match.MatchDateTime.Value)).TotalMinutes) : double.MaxValue)
            .ThenBy(match => string.Equals(match.League, prediction.League, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .First();
    }

    private static string BuildPredictionMatchKey(
        DateOnly? matchLocalDate,
        string? fixtureKey,
        string? league,
        string? homeTeam,
        string? awayTeam,
        bool includeLeague)
    {
        return includeLeague
            ? ResolveFixtureKey(fixtureKey, matchLocalDate, league, homeTeam, awayTeam)
            : ResolveFixtureKey(fixtureKey, matchLocalDate, null, homeTeam, awayTeam);
    }

    private static string BuildSnapshotDuplicateKey(int predictionId, string sourceName)
    {
        return $"{predictionId}|{sourceName}";
    }

    private List<MatchData> DeduplicateMatchesForGeneration(IEnumerable<MatchData> matches, string targetDate)
    {
        return DeduplicateItems(
            matches,
            GetMatchIdentityKey,
            SelectPreferredMatchData,
            duplicateGroups =>
            {
                _logger.LogWarning(
                    "Deduplicated {DuplicateGroupCount} duplicate match fixture groups before prediction generation for {TargetDate}. Samples: {Samples}",
                    duplicateGroups.Count,
                    targetDate,
                    FormatDuplicateSamples(duplicateGroups.Select(group => group.First()), DescribeMatchData));
            });
    }

    private List<PredictionCandidate> DeduplicateForecastCandidates(IEnumerable<PredictionCandidate> candidates, string targetDate)
    {
        return DeduplicateItems(
            candidates,
            GetCandidateObservationKey,
            SelectPreferredCandidate,
            duplicateGroups =>
            {
                _logger.LogWarning(
                    "Deduplicated {DuplicateGroupCount} duplicate forecast candidate groups for {TargetDate}. Samples: {Samples}",
                    duplicateGroups.Count,
                    targetDate,
                    FormatDuplicateSamples(duplicateGroups.Select(group => group.First()), DescribeCandidate));
            });
    }

    private List<PredictionCandidate> DeduplicatePublishedCandidates(IEnumerable<PredictionCandidate> candidates, string targetDate)
    {
        return DeduplicateItems(
            candidates,
            GetCandidatePredictionKey,
            SelectPreferredCandidate,
            duplicateGroups =>
            {
                _logger.LogWarning(
                    "Deduplicated {DuplicateGroupCount} duplicate published prediction groups for {TargetDate}. Samples: {Samples}",
                    duplicateGroups.Count,
                    targetDate,
                    FormatDuplicateSamples(duplicateGroups.Select(group => group.First()), DescribeCandidate));
            });
    }

    private static List<T> DeduplicateItems<T>(
        IEnumerable<T> items,
        Func<T, string> keySelector,
        Func<IEnumerable<T>, T> winnerSelector,
        Action<List<IGrouping<string, T>>> logDuplicates)
    {
        var groups = items
            .GroupBy(keySelector, StringComparer.Ordinal)
            .ToList();

        var duplicateGroups = groups
            .Where(group => group.Count() > 1)
            .ToList();

        if (duplicateGroups.Count > 0)
        {
            logDuplicates(duplicateGroups);
        }

        return groups
            .Select(winnerSelector)
            .ToList();
    }

    private static PredictionCandidate SelectPreferredCandidate(IEnumerable<PredictionCandidate> candidates)
    {
        return candidates
            .OrderByDescending(candidate => candidate.WasPublished)
            .ThenByDescending(candidate => candidate.CalibratedProbability)
            .ThenByDescending(candidate => candidate.RawProbability)
            .ThenByDescending(candidate => candidate.ThresholdUsed)
            .ThenByDescending(candidate => candidate.MatchDateTime ?? DateTime.MinValue)
            .ThenByDescending(candidate => candidate.MatchLocalTime ?? TimeOnly.MinValue)
            .First();
    }

    private static MatchData SelectPreferredMatchData(IEnumerable<MatchData> matches)
    {
        return matches
            .OrderByDescending(GetMatchDataCompletenessScore)
            .ThenByDescending(match => match.MatchDateTime ?? DateTime.MinValue)
            .ThenByDescending(match => match.Id)
            .First();
    }

    private static int GetMatchDataCompletenessScore(MatchData match)
    {
        var score = 0;
        score += !string.IsNullOrWhiteSpace(match.FixtureKey) ? 4 : 0;
        score += match.MatchDateTime.HasValue ? 3 : 0;
        score += match.MatchLocalTime.HasValue ? 1 : 0;
        score += CountPositive(
            match.HomeWin,
            match.Draw,
            match.AwayWin,
            match.OverTwoGoals,
            match.UnderTwoGoals,
            match.OverThreeGoals,
            match.UnderThreeGoals,
            match.BttsYes,
            match.BttsNo,
            match.OverOneGoal,
            match.OverOnePointFive,
            match.UnderOnePointFive,
            match.AhZeroHome,
            match.AhZeroAway,
            match.AhMinusHalfHome,
            match.AhMinusHalfAway,
            match.AhMinusOneHome,
            match.AhMinusOneAway,
            match.AhPlusHalfHome,
            match.AhPlusHalfAway);
        return score;
    }

    private static int CountPositive(params double[] values)
    {
        return values.Count(value => value > 0);
    }

    private static string GetMatchIdentityKey(MatchData match)
    {
        return ResolveFixtureKey(
            match.FixtureKey,
            match.MatchLocalDate,
            match.League,
            match.HomeTeam,
            match.AwayTeam);
    }

    private static string ResolveFixtureKey(
        string? fixtureKey,
        DateOnly? localDate,
        string? league,
        string? homeTeam,
        string? awayTeam)
    {
        if (!string.IsNullOrWhiteSpace(fixtureKey))
        {
            return fixtureKey.Trim();
        }

        return string.Join(
            "|",
            localDate?.ToString("yyyy-MM-dd") ?? "unknown-date",
            NormalizeFixtureKeyPart(league),
            NormalizeFixtureKeyPart(homeTeam),
            NormalizeFixtureKeyPart(awayTeam));
    }

    private static string NormalizeFixtureKeyPart(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value.Trim().ToLowerInvariant();
    }

    private static string FormatDuplicateSamples<T>(IEnumerable<T> items, Func<T, string> formatter)
    {
        return string.Join("; ", items.Take(5).Select(formatter));
    }

    private static string DescribeCandidate(PredictionCandidate candidate)
    {
        return $"{candidate.HomeTeam} vs {candidate.AwayTeam} [{candidate.League}] {candidate.PredictionCategory}/{candidate.Market} key={candidate.FixtureKey} cal={candidate.CalibratedProbability:F3}";
    }

    private static string DescribeMatchData(MatchData match)
    {
        return $"{match.HomeTeam} vs {match.AwayTeam} [{match.League}] key={match.FixtureKey} time={match.Time} score={GetMatchDataCompletenessScore(match)}";
    }

    private bool ApplyDecisionBackfill(Prediction prediction, PredictionMarket market)
    {
        var updated = false;

        if (string.IsNullOrWhiteSpace(prediction.CalibratorUsed) || prediction.CalibratorUsed == "Unknown")
        {
            prediction.CalibratorUsed = "Bucket";
            updated = true;
        }

        if (prediction.ThresholdUsed <= 0)
        {
            prediction.ThresholdUsed = ResolveFallbackThreshold(market);
            updated = true;
        }

        if (string.IsNullOrWhiteSpace(prediction.ThresholdSource) || prediction.ThresholdSource == "Unknown")
        {
            prediction.ThresholdSource = "Configured";
            updated = true;
        }

        if (!prediction.WasPublished)
        {
            prediction.WasPublished = true;
            updated = true;
        }

        return updated;
    }

    private bool ApplyDecisionBackfill(ForecastObservation forecast, PredictionMarket market)
    {
        var updated = false;

        if (string.IsNullOrWhiteSpace(forecast.CalibratorUsed) || forecast.CalibratorUsed == "Unknown")
        {
            forecast.CalibratorUsed = "Bucket";
            updated = true;
        }

        if (forecast.ThresholdUsed <= 0)
        {
            forecast.ThresholdUsed = ResolveFallbackThreshold(market);
            updated = true;
        }

        if (string.IsNullOrWhiteSpace(forecast.ThresholdSource) || forecast.ThresholdSource == "Unknown")
        {
            forecast.ThresholdSource = "Configured";
            updated = true;
        }

        return updated;
    }

    private bool TryResolvePredictionMarket(Prediction prediction, out PredictionMarket market)
    {
        market = prediction.PredictionCategory switch
        {
            "BothTeamsScore" => PredictionMarket.BothTeamsScore,
            "Over2.5Goals" => PredictionMarket.Over25Goals,
            "Under2.5Goals" => PredictionMarket.Under25Goals,
            "Draw" => PredictionMarket.Draw,
            "StraightWin" when prediction.PredictedOutcome == "Home Win" => PredictionMarket.HomeWin,
            "StraightWin" when prediction.PredictedOutcome == "Away Win" => PredictionMarket.AwayWin,
            _ => default
        };

        return prediction.PredictionCategory is "BothTeamsScore" or "Over2.5Goals" or "Under2.5Goals" or "Draw" ||
               (prediction.PredictionCategory == "StraightWin" && prediction.PredictedOutcome is "Home Win" or "Away Win");
    }

    private double ResolveFallbackThreshold(PredictionMarket market)
    {
        return market switch
        {
            PredictionMarket.BothTeamsScore => _predictionSettings.BttsScoreThreshold,
            PredictionMarket.Over25Goals => _predictionSettings.OverTwoGoalsStrongThreshold,
            PredictionMarket.Under25Goals => _predictionSettings.UnderTwoGoalsStrongThreshold,
            PredictionMarket.Draw => _predictionSettings.DrawStrongThreshold,
            PredictionMarket.HomeWin => _predictionSettings.HomeWinStrong,
            PredictionMarket.AwayWin => _predictionSettings.AwayWinStrong,
            _ => 0.0
        };
    }
    
    private async Task SaveRegressionPredictions(IEnumerable<RegressionPrediction> predictions)
    {
        var predictionList = predictions.ToList();
        if (!predictionList.Any()) return;

        // 1. Extract unique dates to fetch existing records in ONE bulk query
        var uniqueDates = predictionList.Select(p => p.Date).Distinct().ToList();

        var existingPredictions = await _dbContext.RegressionPredictions
            .Where(p => uniqueDates.Contains(p.Date))
            .ToListAsync();

        // 2. Create a Dictionary for O(1) memory lookups using your 6-part composite key
        var existingDict = existingPredictions
            .GroupBy(p => (
                p.HomeTeam, 
                p.AwayTeam, 
                p.League, 
                p.Date, 
                p.Time, 
                p.PredictionCategory))
            .ToDictionary(g => g.Key, g => g.First());

        // 3. Loop through the memory collection, not the database
        foreach (var prediction in predictionList)
        {
            var key = (
                prediction.HomeTeam, 
                prediction.AwayTeam, 
                prediction.League, 
                prediction.Date, 
                prediction.Time, 
                prediction.PredictionCategory);

            if (existingDict.TryGetValue(key, out var existingRecord))
            {
                // UPDATE SCENARIO: The prediction already exists.
                existingRecord.PredictedOutcome = prediction.PredictedOutcome;
                existingRecord.ConfidenceScore = prediction.ConfidenceScore;
                existingRecord.ExpectedHomeGoals = prediction.ExpectedHomeGoals;
                existingRecord.ExpectedAwayGoals = prediction.ExpectedAwayGoals;
            }
            else
            {
                // INSERT SCENARIO
                _dbContext.RegressionPredictions.Add(prediction);
                
                // Add to dictionary to prevent duplicate inserts if the incoming list 
                // accidentally contains the exact same prediction twice
                existingDict[key] = prediction; 
            }
        }

        // 4. Save all inserts and updates in a single transaction
        await _dbContext.SaveChangesAsync();
    }

    private static string Norm(string? s) =>
        (s ?? "").Trim().ToLowerInvariant();

    private static (string Date, string Home, string Away, string League) CreateScoreFixtureKey(
        string? date,
        string? homeTeam,
        string? awayTeam,
        string? league)
    {
        return (
            date ?? string.Empty,
            ScoreMatchingHelper.CreateTeamLookupKey(homeTeam, league),
            ScoreMatchingHelper.CreateTeamLookupKey(awayTeam, league),
            ScoreMatchingHelper.CreateLeagueLookupKey(league));
    }

    private static DateTime? ResolveScheduledMatchTime(DateOnly? localDate, TimeOnly? localTime, DateTime? matchDateTime)
    {
        if (matchDateTime.HasValue)
        {
            return matchDateTime.Value;
        }

        if (!localDate.HasValue || !localTime.HasValue)
        {
            return null;
        }

        var localDateTime = localDate.Value.ToDateTime(localTime.Value);
        return DateTimeProvider.ConvertLocalToUtc(localDateTime);
    }

    private static double GetMatchTimeScore(DateTime? targetMatchTime, DateTime? candidateMatchTime)
    {
        if (!targetMatchTime.HasValue || !candidateMatchTime.HasValue)
        {
            return 0;
        }

        var minutesApart = Math.Abs((candidateMatchTime.Value - targetMatchTime.Value).TotalMinutes);
        if (minutesApart <= 10) return 1.0;
        if (minutesApart <= 45) return 0.6;
        if (minutesApart <= 120) return 0.25;
        return 0;
    }

    private static T? FindBestFixtureCandidate<T>(
        FixtureCandidateIndex<T> candidateIndex,
        string homeTeam,
        string awayTeam,
        string? league,
        string? targetDate,
        DateTime? targetMatchTime,
        Func<T, string> homeSelector,
        Func<T, string> awaySelector,
        Func<T, string?> leagueSelector,
        Func<T, DateTime?> matchTimeSelector,
        Func<T, bool> isLiveSelector,
        Func<T, double>? qualityScoreSelector = null)
        where T : class
    {
        var targetHomeKey = ScoreMatchingHelper.CreateTeamLookupKey(homeTeam, league);
        var targetAwayKey = ScoreMatchingHelper.CreateTeamLookupKey(awayTeam, league);

        var exactCandidates = candidateIndex.GetExactPairCandidates(targetHomeKey, targetAwayKey).ToList();
        var scopedExactCandidates = exactCandidates
            .Where(candidate => ExactCandidateMatchesTargetDate(candidate, targetDate, matchTimeSelector))
            .ToList();

        if (scopedExactCandidates.Count == 1)
        {
            return scopedExactCandidates[0];
        }

        if (scopedExactCandidates.Count > 1)
        {
            return scopedExactCandidates.All(isLiveSelector)
                ? scopedExactCandidates
                    .OrderByDescending(candidate => matchTimeSelector(candidate) ?? DateTime.MinValue)
                    .First()
                : SelectBestFixtureCandidate(
                    scopedExactCandidates,
                    homeTeam,
                    awayTeam,
                    league,
                    targetMatchTime,
                    homeSelector,
                    awaySelector,
                    leagueSelector,
                    matchTimeSelector,
                    isLiveSelector,
                    qualityScoreSelector);
        }

        if (exactCandidates.Count == 1 && string.IsNullOrWhiteSpace(targetDate))
        {
            return exactCandidates[0];
        }

        var scopedCandidates = candidateIndex.GetScopedCandidates(targetDate, league, targetMatchTime);

        return SelectBestFixtureCandidate(
            scopedCandidates.Count > 0 ? scopedCandidates : candidateIndex.AllCandidates,
            homeTeam,
            awayTeam,
            league,
            targetMatchTime,
            homeSelector,
            awaySelector,
            leagueSelector,
            matchTimeSelector,
            isLiveSelector,
            qualityScoreSelector);
    }

    private static T? SelectBestFixtureCandidate<T>(
        IEnumerable<T> candidates,
        string homeTeam,
        string awayTeam,
        string? league,
        DateTime? targetMatchTime,
        Func<T, string> homeSelector,
        Func<T, string> awaySelector,
        Func<T, string?> leagueSelector,
        Func<T, DateTime?> matchTimeSelector,
        Func<T, bool> isLiveSelector,
        Func<T, double>? qualityScoreSelector = null)
    {
        var scoredCandidates = new List<(T Candidate, double BaseScore, double TotalScore, bool ExactPair)>();

        foreach (var candidate in candidates)
        {
            var candidateLeague = leagueSelector(candidate);
            var homeMatch = ScoreMatchingHelper.GetTeamMatchResult(homeTeam, homeSelector(candidate), league, candidateLeague);
            var awayMatch = ScoreMatchingHelper.GetTeamMatchResult(awayTeam, awaySelector(candidate), league, candidateLeague);
            if (!homeMatch.IsMatch || !awayMatch.IsMatch)
            {
                continue;
            }

            var baseScore = (homeMatch.Score + awayMatch.Score) / 2.0;
            var exactPair = homeMatch.IsExactKeyMatch && awayMatch.IsExactKeyMatch;
            var leagueScore = ScoreMatchingHelper.GetLeagueMatchScore(league, candidateLeague);
            var timeScore = GetMatchTimeScore(targetMatchTime, matchTimeSelector(candidate));
            var statusScore = isLiveSelector(candidate) ? 0.0 : 0.30;
            var qualityScore = qualityScoreSelector?.Invoke(candidate) ?? 0.5;
            var totalScore = baseScore + (exactPair ? 0.20 : 0.0) + (leagueScore * 0.15) + (timeScore * 0.10) + statusScore + ((qualityScore - 0.5) * 0.10);

            scoredCandidates.Add((candidate, baseScore, totalScore, exactPair));
        }

        if (scoredCandidates.Count == 0)
        {
            return default;
        }

        var ordered = scoredCandidates
            .OrderByDescending(candidate => candidate.TotalScore)
            .ThenByDescending(candidate => candidate.BaseScore)
            .ToList();

        var best = ordered[0];
        if (!best.ExactPair && best.BaseScore < 0.84)
        {
            return default;
        }

        if (ordered.Count == 1)
        {
            return best.Candidate;
        }

        var runnerUp = ordered[1];
        var requiredMargin = best.ExactPair ? 0.05 : 0.12;
        return best.TotalScore - runnerUp.TotalScore >= requiredMargin
            ? best.Candidate
            : default;
    }

    private static bool ExactCandidateMatchesTargetDate<T>(
        T candidate,
        string? targetDate,
        Func<T, DateTime?> matchTimeSelector)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(targetDate))
        {
            return true;
        }

        var matchTime = matchTimeSelector(candidate);
        if (!matchTime.HasValue)
        {
            return false;
        }

        return string.Equals(
            DateTimeProvider.ConvertUtcToLocal(matchTime.Value).ToString("dd-MM-yyyy"),
            targetDate,
            StringComparison.Ordinal);
    }

    private static List<T> ConsolidateFixtureSnapshots<T>(
        IEnumerable<T> candidates,
        Func<T, string> homeSelector,
        Func<T, string> awaySelector,
        Func<T, string?> leagueSelector,
        Func<T, DateTime?> matchTimeSelector,
        Func<T, bool> isLiveSelector)
        where T : class
    {
        return candidates
            .GroupBy(candidate => CreateScoreFixtureKey(
                matchTimeSelector(candidate).HasValue
                    ? DateTimeProvider.ConvertUtcToLocal(matchTimeSelector(candidate)!.Value).ToString("dd-MM-yyyy")
                    : string.Empty,
                homeSelector(candidate),
                awaySelector(candidate),
                leagueSelector(candidate)))
            .Select(group => group
                .OrderBy(candidate => isLiveSelector(candidate) ? 1 : 0)
                .ThenByDescending(candidate => matchTimeSelector(candidate) ?? DateTime.MinValue)
                .First())
            .ToList();
    }

    private static (string Date, string Home, string Away, string League) GetStoredScoreSnapshotKey<T>(T score)
        where T : class
    {
        var (matchTime, homeTeam, awayTeam, league) = score switch
        {
            MatchScore flashScore => (flashScore.MatchTime, flashScore.HomeTeam, flashScore.AwayTeam, flashScore.League),
            AiScoreMatchScore aiScore => (aiScore.MatchTime, aiScore.HomeTeam, aiScore.AwayTeam, aiScore.League),
            SofaScoreMatchScore sofaScore => (sofaScore.MatchTime, sofaScore.HomeTeam, sofaScore.AwayTeam, sofaScore.League),
            _ => throw new ArgumentOutOfRangeException(nameof(score), "Unsupported stored score type.")
        };

        var localDate = DateTimeProvider.ConvertUtcToLocal(matchTime).ToString("dd-MM-yyyy");
        return CreateScoreFixtureKey(localDate, homeTeam, awayTeam, league);
    }

    private static DateTime ResolvePreferredStoredMatchTime(DateTime existingMatchTime, DateTime incomingMatchTime, bool existingIsLive, bool incomingIsLive)
    {
        if (!incomingIsLive)
        {
            return incomingMatchTime;
        }

        if (!existingIsLive)
        {
            return existingMatchTime;
        }

        return incomingMatchTime < existingMatchTime ? incomingMatchTime : existingMatchTime;
    }

    private static bool ShouldOverwriteStoredScore<T>(
        string existingScore,
        bool existingBttsLabel,
        bool existingIsLive,
        DateTime existingMatchTime,
        T incomingScore)
        where T : class
    {
        var incomingScoreValue = incomingScore switch
        {
            MatchScore flashScore => flashScore.Score,
            AiScoreMatchScore aiScore => aiScore.Score,
            SofaScoreMatchScore sofaScore => sofaScore.Score,
            _ => string.Empty
        };
        var incomingBttsLabel = incomingScore switch
        {
            MatchScore flashScore => flashScore.BTTSLabel,
            AiScoreMatchScore aiScore => aiScore.BTTSLabel,
            SofaScoreMatchScore sofaScore => sofaScore.BTTSLabel,
            _ => false
        };
        var incomingIsLive = incomingScore switch
        {
            MatchScore flashScore => flashScore.IsLive,
            AiScoreMatchScore aiScore => aiScore.IsLive,
            SofaScoreMatchScore sofaScore => sofaScore.IsLive,
            _ => true
        };
        var incomingMatchTime = incomingScore switch
        {
            MatchScore flashScore => flashScore.MatchTime,
            AiScoreMatchScore aiScore => aiScore.MatchTime,
            SofaScoreMatchScore sofaScore => sofaScore.MatchTime,
            _ => existingMatchTime
        };

        if (!existingIsLive && incomingIsLive)
        {
            return false;
        }

        if (existingIsLive && !incomingIsLive)
        {
            return true;
        }

        if (existingScore != incomingScoreValue || existingBttsLabel != incomingBttsLabel)
        {
            return incomingMatchTime >= existingMatchTime;
        }

        return false;
    }

    private static T? ResolveExtendedExactFinishedCandidate<T>(
        IReadOnlyList<RankedExactFinishedCandidate<T>> rankedCandidates)
        where T : class
    {
        if (rankedCandidates.Count == 0)
        {
            return default;
        }

        var best = rankedCandidates[0];
        if (best.MinutesApart <= ExactFinishedRepairWindowMinutes)
        {
            return best.Candidate;
        }

        if (best.MinutesApart > ExtendedExactFinishedRepairWindowMinutes)
        {
            return default;
        }

        var extendedWindowCandidates = rankedCandidates
            .Where(candidate => candidate.MinutesApart <= ExtendedExactFinishedRepairWindowMinutes)
            .ToList();

        if (extendedWindowCandidates.Count == 1)
        {
            return best.Candidate;
        }

        var distinctScores = extendedWindowCandidates
            .Select(candidate => NormalizeSettledScore(candidate.Score))
            .Where(score => !string.IsNullOrWhiteSpace(score))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return distinctScores.Count == 1
            ? best.Candidate
            : default;
    }

    private static string NormalizeSettledScore(string? score)
    {
        return string.IsNullOrWhiteSpace(score)
            ? string.Empty
            : score.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    private object? ChooseBestExactSourceCandidate(
        SettlementFixtureGroup fixture,
        MatchScore? flashCandidate,
        AiScoreMatchScore? aiCandidate,
        IReadOnlyDictionary<(string SourceName, string LeagueKey, string TimeBucketKey), SourceQualityProfile> sourceQualityLookup)
    {
        if (flashCandidate is null)
        {
            return aiCandidate;
        }

        if (aiCandidate is null)
        {
            return flashCandidate;
        }

        var flashScore = GetExactSourceCandidateRank(
            fixture,
            "FlashScore",
            flashCandidate.League,
            flashCandidate.MatchTime,
            sourceQualityLookup);
        var aiScore = GetExactSourceCandidateRank(
            fixture,
            "AiScore",
            aiCandidate.League,
            aiCandidate.MatchTime,
            sourceQualityLookup);

        return aiScore - flashScore >= 0.05 ? aiCandidate : flashCandidate;
    }

    private object? ChooseBestLiveSourceCandidate(
        MatchScore? flashCandidate,
        AiScoreMatchScore? aiCandidate,
        IReadOnlyDictionary<(string SourceName, string LeagueKey, string TimeBucketKey), SourceQualityProfile> sourceQualityLookup)
    {
        if (flashCandidate is null)
        {
            return aiCandidate;
        }

        if (aiCandidate is null)
        {
            return flashCandidate;
        }

        var flashReliability = GetSourceQualityReliability(sourceQualityLookup, "FlashScore", flashCandidate.League, flashCandidate.MatchTime);
        var aiReliability = GetSourceQualityReliability(sourceQualityLookup, "AiScore", aiCandidate.League, aiCandidate.MatchTime);

        if (aiReliability - flashReliability >= 0.08)
        {
            return aiCandidate;
        }

        return aiReliability > flashReliability &&
               aiCandidate.MatchTime >= flashCandidate.MatchTime
            ? aiCandidate
            : flashCandidate;
    }

    private double GetExactSourceCandidateRank(
        SettlementFixtureGroup fixture,
        string sourceName,
        string? candidateLeague,
        DateTime? candidateMatchTimeUtc,
        IReadOnlyDictionary<(string SourceName, string LeagueKey, string TimeBucketKey), SourceQualityProfile> sourceQualityLookup)
    {
        var reliability = GetSourceQualityReliability(sourceQualityLookup, sourceName, candidateLeague, candidateMatchTimeUtc);
        var leagueScore = ScoreMatchingHelper.GetLeagueMatchScore(fixture.League, candidateLeague);
        var timeScore = GetMatchTimeScore(fixture.ScheduledMatchTimeUtc, candidateMatchTimeUtc);
        return (reliability * 0.55) + (timeScore * 0.30) + (leagueScore * 0.15);
    }

    private async Task<SourceQualityRebuildResult> RebuildSourceQualityProfilesAsync(int lookbackDays = 30)
    {
        var today = DateOnly.FromDateTime(DateTimeProvider.GetLocalTime());
        var earliestSettlementDate = today.AddDays(-Math.Max(lookbackDays, 1));
        var startOfWindowUtc = DateTimeProvider.ConvertLocalToUtc(
            earliestSettlementDate.ToDateTime(new TimeOnly(0, 0), DateTimeKind.Unspecified));
        var endOfWindowUtc = DateTimeProvider.ConvertLocalToUtc(
            today.AddDays(1).ToDateTime(new TimeOnly(0, 0), DateTimeKind.Unspecified));

        var settledPredictions = await _dbContext.Predictions
            .AsNoTracking()
            .Where(prediction =>
                prediction.IsCurrentRevision &&
                prediction.MatchLocalDate >= earliestSettlementDate &&
                !prediction.IsLive &&
                !string.IsNullOrWhiteSpace(prediction.ActualScore))
            .ToListAsync();

        if (settledPredictions.Count == 0)
        {
            var clearedEmptyProfiles = await ReplaceSourceQualityProfilesAsync([]);
            return clearedEmptyProfiles
                ? new SourceQualityRebuildResult(true, 0, "No settled predictions were available in the source-quality lookback window.")
                : SourceQualityRebuildResult.Skipped("The SourceQualityProfiles table is missing.");
        }

        var fixtures = BuildSettlementFixtureGroups(settledPredictions, []);
        var flashScores = await _dbContext.MatchScores
            .AsNoTracking()
            .Where(score => score.MatchTime >= startOfWindowUtc && score.MatchTime < endOfWindowUtc)
            .ToListAsync();
        var aiScores = await _dbContext.AiScoreMatchScores
            .AsNoTracking()
            .Where(score => score.MatchTime >= startOfWindowUtc && score.MatchTime < endOfWindowUtc)
            .ToListAsync();
        List<SofaScoreMatchScore> sofaScores;
        try
        {
            sofaScores = await _dbContext.SofaScoreMatchScores
                .AsNoTracking()
                .Where(score => score.MatchTime >= startOfWindowUtc && score.MatchTime < endOfWindowUtc)
                .ToListAsync();
        }
        catch (PostgresException ex) when (IsMissingSofaScoreTable(ex))
        {
            _logger.LogWarning(
                "Skipping source quality profile refresh because the SofaScoreMatchScores table is missing. Apply the latest EF migration to enable SofaScore source-quality training.");
            return SourceQualityRebuildResult.Skipped("The SofaScoreMatchScores table is missing.");
        }

        var flashFinishedIndex = BuildExactFinishedCandidateIndex(
            flashScores.Where(score => !score.IsLive),
            score => score.HomeTeam,
            score => score.AwayTeam,
            score => score.MatchTime,
            score => score.League);
        var flashLiveIndex = BuildExactFinishedCandidateIndex(
            flashScores.Where(score => score.IsLive),
            score => score.HomeTeam,
            score => score.AwayTeam,
            score => score.MatchTime,
            score => score.League);
        var aiFinishedIndex = BuildExactFinishedCandidateIndex(
            aiScores.Where(score => !score.IsLive),
            score => score.HomeTeam,
            score => score.AwayTeam,
            score => score.MatchTime,
            score => score.League);
        var aiLiveIndex = BuildExactFinishedCandidateIndex(
            aiScores.Where(score => score.IsLive),
            score => score.HomeTeam,
            score => score.AwayTeam,
            score => score.MatchTime,
            score => score.League);
        var sofaFinishedIndex = BuildExactFinishedCandidateIndex(
            sofaScores.Where(score => !score.IsLive),
            score => score.HomeTeam,
            score => score.AwayTeam,
            score => score.MatchTime,
            score => score.League);
        var sofaLiveIndex = BuildExactFinishedCandidateIndex(
            sofaScores.Where(score => score.IsLive),
            score => score.HomeTeam,
            score => score.AwayTeam,
            score => score.MatchTime,
            score => score.League);

        var accumulators = new Dictionary<(string SourceName, string LeagueKey, string TimeBucketKey), SourceQualityAccumulator>();

        foreach (var fixture in fixtures)
        {
            var settledScore = fixture.Predictions
                .Select(prediction => NormalizeSettledScore(prediction.ActualScore))
                .FirstOrDefault(score => !string.IsNullOrWhiteSpace(score));

            if (string.IsNullOrWhiteSpace(settledScore))
            {
                continue;
            }

            RecordSourceQualitySample(
                accumulators,
                "FlashScore",
                fixture,
                FindExactFinishedSourceCandidate(
                    fixture,
                    flashFinishedIndex,
                    score => score.MatchTime,
                    score => score.League,
                    score => score.Score),
                FindLatestExactLiveSourceCandidate(
                    fixture,
                    flashLiveIndex,
                    score => score.MatchTime),
                score => score.Score,
                score => score.MatchTime,
                settledScore);

            RecordSourceQualitySample(
                accumulators,
                "AiScore",
                fixture,
                FindExactFinishedSourceCandidate(
                    fixture,
                    aiFinishedIndex,
                    score => score.MatchTime,
                    score => score.League,
                    score => score.Score),
                FindLatestExactLiveSourceCandidate(
                    fixture,
                    aiLiveIndex,
                    score => score.MatchTime),
                score => score.Score,
                score => score.MatchTime,
                settledScore);

            RecordSourceQualitySample(
                accumulators,
                "SofaScore",
                fixture,
                FindExactFinishedSourceCandidate(
                    fixture,
                    sofaFinishedIndex,
                    score => score.MatchTime,
                    score => score.League,
                    score => score.Score),
                FindLatestExactLiveSourceCandidate(
                    fixture,
                    sofaLiveIndex,
                    score => score.MatchTime),
                score => score.Score,
                score => score.MatchTime,
                settledScore);
        }

        var nowUtc = DateTime.UtcNow;
        var profiles = accumulators.Values
            .Select(accumulator => accumulator.ToProfile(nowUtc))
            .Where(profile => profile.SampleCount > 0)
            .ToList();

        var replacedProfiles = await ReplaceSourceQualityProfilesAsync(profiles);
        return replacedProfiles
            ? new SourceQualityRebuildResult(true, profiles.Count, "Source quality profiles rebuilt successfully.")
            : SourceQualityRebuildResult.Skipped("The SourceQualityProfiles table is missing.");
    }

    private void RecordSourceQualitySample<T>(
        IDictionary<(string SourceName, string LeagueKey, string TimeBucketKey), SourceQualityAccumulator> accumulators,
        string sourceName,
        SettlementFixtureGroup fixture,
        T? finishedCandidate,
        T? liveCandidate,
        Func<T, string?> scoreSelector,
        Func<T, DateTime?> matchTimeSelector,
        string settledScore)
        where T : class
    {
        var exactLeagueKey = NormalizeLeagueKey(fixture.League);
        var timeBucket = GetTimeBucket(fixture.ScheduledMatchTimeUtc);
        var applicableKeys = new[]
        {
            (SourceName: sourceName, LeagueKey: exactLeagueKey, TimeBucketKey: timeBucket.Key),
            (SourceName: sourceName, LeagueKey: exactLeagueKey, TimeBucketKey: "all"),
            (SourceName: sourceName, LeagueKey: "all", TimeBucketKey: timeBucket.Key),
            (SourceName: sourceName, LeagueKey: "all", TimeBucketKey: "all")
        };

        foreach (var key in applicableKeys)
        {
            if (!accumulators.TryGetValue(key, out var accumulator))
            {
                accumulator = new SourceQualityAccumulator(
                    key.SourceName,
                    key.LeagueKey,
                    key.LeagueKey == "all" ? "All Leagues" : fixture.League,
                    key.TimeBucketKey,
                    key.TimeBucketKey == "all" ? "All Kickoffs" : timeBucket.Label);
                accumulators[key] = accumulator;
            }

            accumulator.RecordSample(
                finishedCandidate is not null,
                finishedCandidate is not null &&
                string.Equals(NormalizeSettledScore(scoreSelector(finishedCandidate)), settledScore, StringComparison.Ordinal),
                finishedCandidate is null && liveCandidate is not null,
                finishedCandidate is not null && fixture.ScheduledMatchTimeUtc.HasValue && matchTimeSelector(finishedCandidate).HasValue
                    ? Math.Abs((matchTimeSelector(finishedCandidate)!.Value - fixture.ScheduledMatchTimeUtc.Value).TotalMinutes)
                    : null);
        }
    }

    private async Task<bool> ReplaceSourceQualityProfilesAsync(IReadOnlyCollection<SourceQualityProfile> profiles)
    {
        try
        {
            await _dbContext.SourceQualityProfiles.ExecuteDeleteAsync();
        }
        catch (InvalidOperationException)
        {
            var existingProfiles = await _dbContext.SourceQualityProfiles.ToListAsync();
            _dbContext.SourceQualityProfiles.RemoveRange(existingProfiles);
        }
        catch (PostgresException ex) when (IsMissingSourceQualityTable(ex))
        {
            _logger.LogWarning(
                "Skipping source quality profile refresh because the SourceQualityProfiles table is missing. Apply the latest EF migration to enable this feature.");
            return false;
        }

        if (profiles.Count > 0)
        {
            await _dbContext.SourceQualityProfiles.AddRangeAsync(profiles);
        }

        await _dbContext.SaveChangesAsync();
        return true;
    }

    private async Task SaveSofaScoreMatchScores(List<SofaScoreMatchScore> scores)
    {
        if (scores.Count == 0)
        {
            return;
        }

        var minTime = scores.Min(s => s.MatchTime);
        var maxTime = scores.Max(s => s.MatchTime);

        List<SofaScoreMatchScore> existingScoresList;
        try
        {
            existingScoresList = await _dbContext.SofaScoreMatchScores
                .Where(s => s.MatchTime >= minTime && s.MatchTime <= maxTime)
                .ToListAsync();
        }
        catch (PostgresException ex) when (IsMissingSofaScoreTable(ex))
        {
            _logger.LogWarning(
                "Skipping SofaScore score persistence because the SofaScoreMatchScores table is missing. Apply the latest EF migration to enable SofaScore source-quality history.");
            return;
        }

        var existingScoresDict = existingScoresList
            .GroupBy(GetStoredScoreSnapshotKey)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var incomingScore in scores)
        {
            var key = GetStoredScoreSnapshotKey(incomingScore);

            if (existingScoresDict.TryGetValue(key, out var existingRecord))
            {
                existingRecord.MatchTime = ResolvePreferredStoredMatchTime(
                    existingRecord.MatchTime,
                    incomingScore.MatchTime,
                    existingRecord.IsLive,
                    incomingScore.IsLive);

                if (ShouldOverwriteStoredScore(
                        existingRecord.Score,
                        existingRecord.BTTSLabel,
                        existingRecord.IsLive,
                        existingRecord.MatchTime,
                        incomingScore))
                {
                    existingRecord.Score = incomingScore.Score;
                    existingRecord.IsLive = incomingScore.IsLive;
                    existingRecord.BTTSLabel = incomingScore.BTTSLabel;
                    existingRecord.DisplayedScore = incomingScore.DisplayedScore;
                    existingRecord.RegularTimeScore = incomingScore.RegularTimeScore;
                    existingRecord.HalfTimeScore = incomingScore.HalfTimeScore;
                    existingRecord.ExtraTimeScore = incomingScore.ExtraTimeScore;
                    existingRecord.StatusText = incomingScore.StatusText;
                    existingRecord.EventUrl = incomingScore.EventUrl;
                    existingRecord.League = string.IsNullOrWhiteSpace(incomingScore.League)
                        ? existingRecord.League
                        : incomingScore.League;
                }
            }
            else
            {
                _dbContext.SofaScoreMatchScores.Add(incomingScore);
                existingScoresDict[key] = incomingScore;
            }
        }

        await _dbContext.SaveChangesAsync();
    }

    private async Task<Dictionary<(string SourceName, string LeagueKey, string TimeBucketKey), SourceQualityProfile>> LoadSourceQualityLookupAsync()
    {
        List<SourceQualityProfile> profiles;
        try
        {
            profiles = await _dbContext.SourceQualityProfiles
                .AsNoTracking()
                .ToListAsync();
        }
        catch (PostgresException ex) when (IsMissingSourceQualityTable(ex))
        {
            _logger.LogWarning(
                "Source quality lookup is unavailable because the SourceQualityProfiles table is missing. The score updater will continue without source-quality weighting until the latest EF migration is applied.");
            return [];
        }

        return profiles.ToDictionary(
            profile => (
                SourceName: NormalizeSourceName(profile.SourceName),
                LeagueKey: string.IsNullOrWhiteSpace(profile.LeagueKey) ? "all" : profile.LeagueKey.Trim().ToLowerInvariant(),
                TimeBucketKey: string.IsNullOrWhiteSpace(profile.TimeBucketKey) ? "all" : profile.TimeBucketKey.Trim().ToLowerInvariant()),
            profile => profile);
    }

    private static double GetSourceQualityReliability(
        IReadOnlyDictionary<(string SourceName, string LeagueKey, string TimeBucketKey), SourceQualityProfile> sourceQualityLookup,
        string sourceName,
        string? league,
        DateTime? kickoffUtc)
    {
        if (sourceQualityLookup.Count == 0)
        {
            return 0.5;
        }

        var normalizedSource = NormalizeSourceName(sourceName);
        var leagueKey = NormalizeLeagueKey(league);
        var timeBucketKey = GetTimeBucket(kickoffUtc).Key;

        foreach (var key in new[]
                 {
                     (normalizedSource, leagueKey, timeBucketKey),
                     (normalizedSource, leagueKey, "all"),
                     (normalizedSource, "all", timeBucketKey),
                     (normalizedSource, "all", "all")
                 })
        {
            if (sourceQualityLookup.TryGetValue(key, out var profile) && profile.SampleCount >= 4)
            {
                return Math.Clamp(profile.ReliabilityScore, 0.0, 1.0);
            }
        }

        return 0.5;
    }

    private static (string Key, string Label) GetTimeBucket(DateTime? kickoffUtc)
    {
        if (!kickoffUtc.HasValue)
        {
            return ("all", "All Kickoffs");
        }

        var localHour = DateTimeProvider.ConvertUtcToLocal(kickoffUtc.Value).Hour;
        return localHour switch
        {
            < 6 => ("night", "00:00-05:59"),
            < 12 => ("morning", "06:00-11:59"),
            < 18 => ("afternoon", "12:00-17:59"),
            _ => ("evening", "18:00-23:59")
        };
    }

    private static string NormalizeSourceName(string sourceName)
    {
        return string.IsNullOrWhiteSpace(sourceName)
            ? "unknown"
            : sourceName.Trim().ToLowerInvariant();
    }

    private static string NormalizeLeagueKey(string? league)
    {
        return string.IsNullOrWhiteSpace(league)
            ? "unknown"
            : league.Trim().ToLowerInvariant();
    }

    private static bool IsMissingSourceQualityTable(PostgresException ex)
    {
        return IsMissingTable(ex, "SourceQualityProfiles");
    }

    private static bool IsMissingSofaScoreTable(PostgresException ex)
    {
        return IsMissingTable(ex, "SofaScoreMatchScores");
    }

    private static bool IsMissingTable(PostgresException ex, string tableName)
    {
        return ex.SqlState == PostgresErrorCodes.UndefinedTable &&
               string.Equals(ex.TableName, tableName, StringComparison.Ordinal);
    }

    private sealed class SettlementFixtureGroup
    {
        public string Date { get; init; } = string.Empty;
        public DateOnly MatchLocalDate { get; init; }
        public string FixtureKey { get; init; } = string.Empty;
        public string HomeTeam { get; init; } = string.Empty;
        public string AwayTeam { get; init; } = string.Empty;
        public string League { get; init; } = string.Empty;
        public DateTime? ScheduledMatchTimeUtc { get; init; }
        public List<Prediction> Predictions { get; } = [];
        public List<ForecastObservation> Forecasts { get; } = [];
    }

    private sealed record RankedExactFinishedCandidate<T>(
        T Candidate,
        DateTime? MatchTime,
        double MinutesApart,
        double LeagueScore,
        string? Score,
        double QualityScore)
        where T : class;

    private sealed class SourceQualityAccumulator
    {
        private double _kickoffOffsetTotal;
        private int _kickoffOffsetCount;

        public SourceQualityAccumulator(
            string sourceName,
            string leagueKey,
            string leagueLabel,
            string timeBucketKey,
            string timeBucketLabel)
        {
            SourceName = sourceName;
            LeagueKey = leagueKey;
            LeagueLabel = leagueLabel;
            TimeBucketKey = timeBucketKey;
            TimeBucketLabel = timeBucketLabel;
        }

        public string SourceName { get; }
        public string LeagueKey { get; }
        public string LeagueLabel { get; }
        public string TimeBucketKey { get; }
        public string TimeBucketLabel { get; }
        public int SampleCount { get; private set; }
        public int FinishedCoverageCount { get; private set; }
        public int ExactScoreMatchCount { get; private set; }
        public int LiveOnlyCount { get; private set; }

        public void RecordSample(bool hasFinishedSource, bool exactScoreMatch, bool isLiveOnly, double? kickoffOffsetMinutes)
        {
            SampleCount++;

            if (hasFinishedSource)
            {
                FinishedCoverageCount++;
            }

            if (exactScoreMatch)
            {
                ExactScoreMatchCount++;
            }

            if (isLiveOnly)
            {
                LiveOnlyCount++;
            }

            if (kickoffOffsetMinutes.HasValue)
            {
                _kickoffOffsetTotal += kickoffOffsetMinutes.Value;
                _kickoffOffsetCount++;
            }
        }

        public SourceQualityProfile ToProfile(DateTime updatedAtUtc)
        {
            var coverageRate = SampleCount > 0 ? FinishedCoverageCount / (double)SampleCount : 0.0;
            var matchRate = FinishedCoverageCount > 0 ? ExactScoreMatchCount / (double)FinishedCoverageCount : 0.0;
            var liveOnlyRate = SampleCount > 0 ? LiveOnlyCount / (double)SampleCount : 0.0;
            var avgOffset = _kickoffOffsetCount > 0 ? _kickoffOffsetTotal / _kickoffOffsetCount : 0.0;
            var offsetPenalty = Math.Clamp(avgOffset / 180.0, 0.0, 1.0);
            var reliability = Math.Clamp(
                (matchRate * 0.55) +
                (coverageRate * 0.30) +
                ((1.0 - liveOnlyRate) * 0.15) -
                (offsetPenalty * 0.10),
                0.0,
                1.0);

            return new SourceQualityProfile
            {
                SourceName = SourceName,
                LeagueKey = LeagueKey,
                LeagueLabel = LeagueLabel,
                TimeBucketKey = TimeBucketKey,
                TimeBucketLabel = TimeBucketLabel,
                SampleCount = SampleCount,
                FinishedCoverageCount = FinishedCoverageCount,
                ExactScoreMatchCount = ExactScoreMatchCount,
                LiveOnlyCount = LiveOnlyCount,
                AverageKickoffOffsetMinutes = avgOffset,
                ReliabilityScore = reliability,
                LastUpdated = updatedAtUtc
            };
        }
    }

    private sealed record SourceQualityRebuildResult(bool Completed, int ProfileCount, string StatusMessage)
    {
        public static SourceQualityRebuildResult Skipped(string reason) => new(false, 0, reason);
    }

    private sealed class FixtureCandidateIndex<T>
        where T : class
    {
        private readonly Dictionary<(string HomeKey, string AwayKey), List<T>> _exactPairLookup;
        private readonly Dictionary<string, List<T>> _dateLookup;
        private readonly Dictionary<(string Date, string LeagueKey), List<T>> _dateLeagueLookup;

        public FixtureCandidateIndex(
            IEnumerable<T> candidates,
            Func<T, string> homeSelector,
            Func<T, string> awaySelector,
            Func<T, string?> leagueSelector,
            Func<T, DateTime?> matchTimeSelector)
        {
            AllCandidates = candidates.ToList();

            _exactPairLookup = AllCandidates
                .GroupBy(candidate => (
                    HomeKey: ScoreMatchingHelper.CreateTeamLookupKey(homeSelector(candidate), leagueSelector(candidate)),
                    AwayKey: ScoreMatchingHelper.CreateTeamLookupKey(awaySelector(candidate), leagueSelector(candidate))))
                .ToDictionary(group => group.Key, group => group.ToList());

            _dateLookup = AllCandidates
                .Where(candidate => matchTimeSelector(candidate).HasValue)
                .GroupBy(candidate => DateTimeProvider.ConvertUtcToLocal(matchTimeSelector(candidate)!.Value).ToString("dd-MM-yyyy"))
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

            _dateLeagueLookup = AllCandidates
                .Where(candidate => matchTimeSelector(candidate).HasValue)
                .GroupBy(candidate => (
                    Date: DateTimeProvider.ConvertUtcToLocal(matchTimeSelector(candidate)!.Value).ToString("dd-MM-yyyy"),
                    LeagueKey: ScoreMatchingHelper.CreateLeagueLookupKey(leagueSelector(candidate))))
                .ToDictionary(group => group.Key, group => group.ToList());
        }

        public IReadOnlyList<T> AllCandidates { get; }

        public IReadOnlyList<T> GetExactPairCandidates(string homeKey, string awayKey)
        {
            return _exactPairLookup.TryGetValue((homeKey, awayKey), out var candidates)
                ? candidates
                : [];
        }

        public IReadOnlyList<T> GetScopedCandidates(string? targetDate, string? league, DateTime? targetMatchTime)
        {
            var datesToTry = new List<string>();

            if (!string.IsNullOrWhiteSpace(targetDate))
            {
                datesToTry.Add(targetDate);
            }

            if (targetMatchTime.HasValue)
            {
                var derivedDate = DateTimeProvider.ConvertUtcToLocal(targetMatchTime.Value).ToString("dd-MM-yyyy");
                if (!datesToTry.Contains(derivedDate, StringComparer.Ordinal))
                {
                    datesToTry.Add(derivedDate);
                }
            }

            var leagueKey = ScoreMatchingHelper.CreateLeagueLookupKey(league);

            foreach (var date in datesToTry)
            {
                if (!string.IsNullOrWhiteSpace(leagueKey) &&
                    _dateLeagueLookup.TryGetValue((date, leagueKey), out var dateLeagueCandidates) &&
                    dateLeagueCandidates.Count > 0)
                {
                    return dateLeagueCandidates;
                }

                if (_dateLookup.TryGetValue(date, out var dateCandidates) && dateCandidates.Count > 0)
                {
                    return dateCandidates;
                }
            }

            return AllCandidates;
        }
    }

    private static MatchData? FindMatchingMatchData(
        DateOnly date,
        string homeTeam,
        string awayTeam,
        string league,
        DateTime? matchDateTime,
        IReadOnlyDictionary<(DateOnly Date, string Home, string Away, string League), MatchData> datedMatches,
        IReadOnlyDictionary<(string Home, string Away, string League), List<MatchData>> teamMatches)
    {
        var datedKey = (
            Date: date,
            Home: Norm(homeTeam),
            Away: Norm(awayTeam),
            League: Norm(league));

        if (datedMatches.TryGetValue(datedKey, out var exactMatch))
        {
            return exactMatch;
        }

        var teamKey = (
            Home: Norm(homeTeam),
            Away: Norm(awayTeam),
            League: Norm(league));

        if (!teamMatches.TryGetValue(teamKey, out var candidates) || candidates.Count == 0)
        {
            return null;
        }

        if (matchDateTime.HasValue)
        {
            return candidates
                .OrderBy(candidate => candidate.MatchDateTime.HasValue
                    ? Math.Abs((candidate.MatchDateTime.Value - matchDateTime.Value).TotalMinutes)
                    : double.MaxValue)
                .FirstOrDefault();
        }

        return candidates.FirstOrDefault();
    }

    private static bool ApplyStoredTime(Prediction prediction, MatchData match)
    {
        var updated = false;

        if (!string.Equals(prediction.Date, match.Date, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(match.Date))
        {
            prediction.Date = match.Date!;
            updated = true;
        }

        if (!string.Equals(prediction.Time, match.Time, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(match.Time))
        {
            prediction.Time = match.Time!;
            updated = true;
        }

        if (prediction.MatchDateTime != match.MatchDateTime && match.MatchDateTime.HasValue)
        {
            prediction.MatchDateTime = match.MatchDateTime;
            updated = true;
        }

        if (match.MatchLocalDate.HasValue && prediction.MatchLocalDate != match.MatchLocalDate.Value)
        {
            prediction.MatchLocalDate = match.MatchLocalDate.Value;
            updated = true;
        }

        if (prediction.MatchLocalTime != match.MatchLocalTime)
        {
            prediction.MatchLocalTime = match.MatchLocalTime;
            updated = true;
        }

        if (!string.Equals(prediction.FixtureKey, match.FixtureKey, StringComparison.Ordinal))
        {
            prediction.FixtureKey = match.FixtureKey;
            updated = true;
        }

        return updated;
    }

    private static bool ApplyStoredTime(ForecastObservation forecast, MatchData match)
    {
        var updated = false;

        if (!string.Equals(forecast.Date, match.Date, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(match.Date))
        {
            forecast.Date = match.Date!;
            updated = true;
        }

        if (!string.Equals(forecast.Time, match.Time, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(match.Time))
        {
            forecast.Time = match.Time!;
            updated = true;
        }

        if (forecast.MatchDateTime != match.MatchDateTime && match.MatchDateTime.HasValue)
        {
            forecast.MatchDateTime = match.MatchDateTime;
            updated = true;
        }

        if (match.MatchLocalDate.HasValue && forecast.MatchLocalDate != match.MatchLocalDate.Value)
        {
            forecast.MatchLocalDate = match.MatchLocalDate.Value;
            updated = true;
        }

        if (forecast.MatchLocalTime != match.MatchLocalTime)
        {
            forecast.MatchLocalTime = match.MatchLocalTime;
            updated = true;
        }

        if (!string.Equals(forecast.FixtureKey, match.FixtureKey, StringComparison.Ordinal))
        {
            forecast.FixtureKey = match.FixtureKey;
            updated = true;
        }

        return updated;
    }

    private static bool ApplyCanonicalFixtureIdentity(MatchData match)
    {
        var identity = FixtureIdentityFactory.FromMatchData(match);
        var updated = false;

        if (identity.MatchLocalDate.HasValue && match.MatchLocalDate != identity.MatchLocalDate)
        {
            match.MatchLocalDate = identity.MatchLocalDate;
            updated = true;
        }

        if (match.MatchLocalTime != identity.MatchLocalTime)
        {
            match.MatchLocalTime = identity.MatchLocalTime;
            updated = true;
        }

        if (identity.MatchLocalDate.HasValue)
        {
            var legacyDate = DateTimeProvider.FormatLocalDate(identity.MatchLocalDate.Value);
            if (!string.Equals(match.Date, legacyDate, StringComparison.Ordinal))
            {
                match.Date = legacyDate;
                updated = true;
            }
        }

        if (identity.MatchLocalTime.HasValue)
        {
            var legacyTime = DateTimeProvider.FormatLocalTime(identity.MatchLocalTime.Value);
            if (!string.Equals(match.Time, legacyTime, StringComparison.Ordinal))
            {
                match.Time = legacyTime;
                updated = true;
            }
        }

        if (!string.Equals(match.FixtureKey, identity.FixtureKey, StringComparison.Ordinal))
        {
            match.FixtureKey = identity.FixtureKey;
            updated = true;
        }

        return updated;
    }

    private static bool ApplyCanonicalFixtureIdentity(Prediction prediction)
    {
        var identity = FixtureIdentityFactory.FromPrediction(prediction);
        var updated = false;

        if (prediction.MatchLocalDate == default && identity.MatchLocalDate.HasValue)
        {
            prediction.MatchLocalDate = identity.MatchLocalDate.Value;
            updated = true;
        }

        if (prediction.MatchLocalTime != identity.MatchLocalTime)
        {
            prediction.MatchLocalTime = identity.MatchLocalTime;
            updated = true;
        }

        if (prediction.MatchLocalDate != default)
        {
            var legacyDate = DateTimeProvider.FormatLocalDate(prediction.MatchLocalDate);
            if (!string.Equals(prediction.Date, legacyDate, StringComparison.Ordinal))
            {
                prediction.Date = legacyDate;
                updated = true;
            }
        }

        if (prediction.MatchLocalTime.HasValue)
        {
            var legacyTime = DateTimeProvider.FormatLocalTime(prediction.MatchLocalTime.Value);
            if (!string.Equals(prediction.Time, legacyTime, StringComparison.Ordinal))
            {
                prediction.Time = legacyTime;
                updated = true;
            }
        }

        if (!string.Equals(prediction.FixtureKey, identity.FixtureKey, StringComparison.Ordinal))
        {
            prediction.FixtureKey = identity.FixtureKey;
            updated = true;
        }

        return updated;
    }

    private static bool ApplyCanonicalFixtureIdentity(ForecastObservation forecast)
    {
        var identity = FixtureIdentityFactory.FromForecast(forecast);
        var updated = false;

        if (forecast.MatchLocalDate == default && identity.MatchLocalDate.HasValue)
        {
            forecast.MatchLocalDate = identity.MatchLocalDate.Value;
            updated = true;
        }

        if (forecast.MatchLocalTime != identity.MatchLocalTime)
        {
            forecast.MatchLocalTime = identity.MatchLocalTime;
            updated = true;
        }

        if (forecast.MatchLocalDate != default)
        {
            var legacyDate = DateTimeProvider.FormatLocalDate(forecast.MatchLocalDate);
            if (!string.Equals(forecast.Date, legacyDate, StringComparison.Ordinal))
            {
                forecast.Date = legacyDate;
                updated = true;
            }
        }

        if (forecast.MatchLocalTime.HasValue)
        {
            var legacyTime = DateTimeProvider.FormatLocalTime(forecast.MatchLocalTime.Value);
            if (!string.Equals(forecast.Time, legacyTime, StringComparison.Ordinal))
            {
                forecast.Time = legacyTime;
                updated = true;
            }
        }

        if (!string.Equals(forecast.FixtureKey, identity.FixtureKey, StringComparison.Ordinal))
        {
            forecast.FixtureKey = identity.FixtureKey;
            updated = true;
        }

        return updated;
    }

    private static bool ApplyCanonicalFixtureIdentity(PredictionCandidate candidate)
    {
        var localDate = candidate.MatchLocalDate != default
            ? candidate.MatchLocalDate
            : DateTimeProvider.ParseLocalDateOrNull(candidate.Date) ?? default;
        var localTime = candidate.MatchLocalTime ?? DateTimeProvider.ParseLocalTimeOrNull(candidate.Time);
        var matchDateTime = candidate.MatchDateTime;

        if (matchDateTime is null && localDate != default)
        {
            var inferredLocalDateTime = localDate.ToDateTime(localTime ?? new TimeOnly(0, 0), DateTimeKind.Unspecified);
            matchDateTime = DateTimeProvider.ConvertLocalToUtc(inferredLocalDateTime);
        }

        var identity = FixtureIdentityFactory.Build(
            candidate.HomeTeam,
            candidate.AwayTeam,
            candidate.League,
            localDate == default ? null : localDate,
            localTime,
            matchDateTime);

        var updated = false;

        if (candidate.MatchLocalDate == default && localDate != default)
        {
            candidate.MatchLocalDate = localDate;
            updated = true;
        }

        if (candidate.MatchLocalTime != localTime)
        {
            candidate.MatchLocalTime = localTime;
            updated = true;
        }

        if (candidate.MatchDateTime != matchDateTime)
        {
            candidate.MatchDateTime = matchDateTime;
            updated = true;
        }

        if (candidate.MatchLocalDate != default)
        {
            var legacyDate = DateTimeProvider.FormatLocalDate(candidate.MatchLocalDate);
            if (!string.Equals(candidate.Date, legacyDate, StringComparison.Ordinal))
            {
                candidate.Date = legacyDate;
                updated = true;
            }
        }

        if (candidate.MatchLocalTime.HasValue)
        {
            var legacyTime = DateTimeProvider.FormatLocalTime(candidate.MatchLocalTime.Value);
            if (!string.Equals(candidate.Time, legacyTime, StringComparison.Ordinal))
            {
                candidate.Time = legacyTime;
                updated = true;
            }
        }

        if (!string.Equals(candidate.FixtureKey, identity.FixtureKey, StringComparison.Ordinal))
        {
            candidate.FixtureKey = identity.FixtureKey;
            updated = true;
        }

        return updated;
    }

    private async Task<PredictionRun> CreatePredictionRunAsync(
        DateOnly targetLocalDate,
        string runReason,
        IReadOnlyCollection<PredictionCandidate> forecastCandidates,
        IReadOnlyCollection<PredictionCandidate> publishedCandidates)
    {
        var localNow = DateTimeProvider.GetLocalTime();
        var predictionRun = new PredictionRun
        {
            TargetLocalDate = targetLocalDate,
            RunKind = "prediction_generation",
            RunLabel = $"{localNow:HH:mm} WAT",
            RunReason = runReason,
            ForecastCount = forecastCandidates.Count,
            PublishedPredictionCount = publishedCandidates.Count,
            StartedAtUtc = DateTime.UtcNow
        };

        await _dbContext.PredictionRuns.AddAsync(predictionRun);
        await _dbContext.SaveChangesAsync();
        return predictionRun;
    }

}
