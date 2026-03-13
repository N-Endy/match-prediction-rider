using System.Globalization;
using Hangfire;
using MatchPredictor.Application.Helpers;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MatchPredictor.Application.Services;

public class AnalyzerService  : IAnalyzerService
{
    private const int ScoreUpdaterLookbackDays = 14;

    private readonly IDataAnalyzerService _dataAnalyzerService;
    private readonly IWebScraperService _webScraperService;
    private readonly ApplicationDbContext _dbContext;
    private readonly IExtractFromExcel _excelExtract;
    private readonly ILogger<AnalyzerService> _logger;
    private readonly IRegressionPredictorService _regressionPredictorService;
    private readonly ICalibrationService _calibrationService;
    private readonly IThresholdTuningService _thresholdTuningService;
    private readonly PredictionSettings _predictionSettings;
    
    public AnalyzerService(
        IDataAnalyzerService dataAnalyzerService,
        IWebScraperService webScraperService,
        ApplicationDbContext dbContext,
        IExtractFromExcel excelExtract,
        IRegressionPredictorService regressionPredictorService,
        ICalibrationService calibrationService,
        IThresholdTuningService thresholdTuningService,
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
        _predictionSettings = predictionOptions.Value;
        _logger = logger;
    }

    [AutomaticRetry(OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public async Task ExtractDataAndSyncDatabaseAsync()
    {
        _logger.LogInformation("Starting data extraction process...");
        try
        {
            await _webScraperService.ScrapeMatchDataAsync();
            _logger.LogInformation("✅ Web scraping for match data completed successfully.");

            var scraped = _excelExtract.ExtractMatchDatasetFromFile().ToList();
            try
            {
                var existingMatches = await _dbContext.MatchDatas
                    .Where(m => scraped.Select(s => s.Date).Distinct().Contains(m.Date!))
                    .ToListAsync();

                var existingMatchLookup = existingMatches
                    .GroupBy(m => (
                        Home: (m.HomeTeam ?? string.Empty).Trim().ToLowerInvariant(),
                        Away: (m.AwayTeam ?? string.Empty).Trim().ToLowerInvariant(),
                        League: (m.League ?? string.Empty).Trim().ToLowerInvariant(),
                        Date: m.Date ?? string.Empty,
                        Time: m.Time ?? string.Empty))
                    .ToDictionary(group => group.Key, group => group.First());

                foreach (var match in scraped)
                {
                    var properDateTime = DateTimeProvider.ParseProperDateAndTime(match.Date, match.Time);
                    match.Date = properDateTime.date;
                    match.Time = properDateTime.time;
                    match.MatchDateTime = properDateTime.utcDateTime;

                    var key = (
                        Home: (match.HomeTeam ?? string.Empty).Trim().ToLowerInvariant(),
                        Away: (match.AwayTeam ?? string.Empty).Trim().ToLowerInvariant(),
                        League: (match.League ?? string.Empty).Trim().ToLowerInvariant(),
                        Date: match.Date ?? string.Empty,
                        Time: match.Time ?? string.Empty);

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
                        existing.MatchDateTime = match.MatchDateTime;
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
            _logger.LogInformation($"Extracted and saved {scraped.Count} matches to DB.");

            // Chain the next job: Generate predictions only after data is successfully synced
            BackgroundJob.Enqueue<IAnalyzerService>(service => service.GeneratePredictionsAsync());
            _logger.LogInformation("Queued GeneratePredictionsAsync background job.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ An error occurred during data scraping and sync.");
            await LogScrapingStatus("Failed", $"Sync Error: {ex.Message}");
            throw;
        }
    }

    [AutomaticRetry(OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public async Task GeneratePredictionsAsync()
    {
        _logger.LogInformation("Starting prediction generation process...");
        try
        {
            await BackfillStoredPredictionTimesAsync(7);
            await BackfillDecisionProvenanceAsync(30);

            var todayStr = DateTimeProvider.GetLocalTime().ToString("dd-MM-yyyy");
            var matches = await _dbContext.MatchDatas
                .Where(match => match.Date == todayStr)
                .ToListAsync();

            var forecastCandidates = _dataAnalyzerService.BuildForecastCandidates(matches);
            var publishedCandidates = _dataAnalyzerService.SelectPublishedPredictions(forecastCandidates);

            await SaveForecastObservations(forecastCandidates, publishedCandidates);
            await SavePredictions(publishedCandidates);
            
            _logger.LogInformation("✅ Predictions calculated and saved successfully.");
            await LogScrapingStatus("Success", "✅ Prediction generation completed successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ An error occurred during prediction calculations.");
            await LogScrapingStatus("Failed", $"Prediction Gen Error: {ex.Message}");
            throw;
        }
    }

    [AutomaticRetry(OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public async Task RunScoreUpdaterAsync()
    {
        _logger.LogInformation("Starting score updating process (every 5m)...");
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
            }
            catch (Exception aiScoreEx)
            {
                _logger.LogWarning(aiScoreEx, "❌ AiScore scraping failed.");
            }

            await UpdatePredictionsWithActualResults();
            _logger.LogInformation("✅ Predictions updated with actual results.");

            await LogScrapingStatus("Success", "✅ Score updating completed successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ An error occurred during score updating.");
            await LogScrapingStatus("Failed", $"Score Update Error: {ex.Message}");
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
                var todayStr = DateTimeProvider.GetLocalTime().ToString("dd-MM-yyyy");
                scraped = await _dbContext.MatchDatas
                    .Where(match => match.Date == todayStr)
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

            await LogScrapingStatus("Success", "✅ Daily analysis completed successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ An error occurred during regression prediction generation.");
            await LogScrapingStatus("Failed", $"Daily Analysis Error: {ex.Message}");
            throw;
        }
    }

    private async Task LogScrapingStatus(string status, string message)
    {
        try
        {
            var log = new ScrapingLog
            {
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

    private async Task UpdatePredictionsWithActualResults()
    {
        var today = DateTimeProvider.GetLocalTime().Date;
        var earliestSettlementDate = today.AddDays(-ScoreUpdaterLookbackDays);
        var settlementDates = Enumerable.Range(0, ScoreUpdaterLookbackDays + 1)
            .Select(offset => earliestSettlementDate.AddDays(offset).ToString("dd-MM-yyyy"))
            .ToHashSet(StringComparer.Ordinal);

        var startOfWindowUtc = DateTimeProvider.ConvertLocalToUtc(earliestSettlementDate);
        var endOfWindowUtc = DateTimeProvider.ConvertLocalToUtc(today.AddDays(1));

        // ── Primary: AiScore (broader league coverage) ──
        var predictionsForSettlement = await _dbContext.Predictions
            .Where(p => settlementDates.Contains(p.Date))
            .ToListAsync();
        var forecastsForSettlement = await _dbContext.ForecastObservations
            .Where(f => settlementDates.Contains(f.Date))
            .ToListAsync();

        foreach (var prediction in predictionsForSettlement)
        {
            RepairPredictionOutcomeFromStoredScore(prediction);
        }

        var aiScores = await _dbContext.AiScoreMatchScores
            .Where(s => s.MatchTime >= startOfWindowUtc && s.MatchTime < endOfWindowUtc)
            .ToListAsync();

        if (aiScores.Count > 0)
        {
            _logger.LogInformation(
                "Matching scores from AiScore ({Count} scores) against {PredCount} predictions in the {LookbackDays}-day settlement window.",
                aiScores.Count,
                predictionsForSettlement.Count,
                ScoreUpdaterLookbackDays);

            foreach (var prediction in predictionsForSettlement)
            {
                var aiMatch = FindBestFixtureCandidate(
                    aiScores,
                    prediction.HomeTeam,
                    prediction.AwayTeam,
                    prediction.League,
                    ResolveScheduledMatchTime(prediction.Date, prediction.Time, prediction.MatchDateTime),
                    score => score.HomeTeam,
                    score => score.AwayTeam,
                    score => score.League,
                    score => score.MatchTime);

                if (aiMatch == null) continue;

                UpdatePredictionSettlementState(prediction, aiMatch.Score, aiMatch.BTTSLabel, aiMatch.IsLive);
            }

            foreach (var forecast in forecastsForSettlement)
            {
                var aiMatch = FindBestFixtureCandidate(
                    aiScores,
                    forecast.HomeTeam,
                    forecast.AwayTeam,
                    forecast.League,
                    ResolveScheduledMatchTime(forecast.Date, forecast.Time, forecast.MatchDateTime),
                    score => score.HomeTeam,
                    score => score.AwayTeam,
                    score => score.League,
                    score => score.MatchTime);

                if (aiMatch == null) continue;

                UpdateForecastObservationState(forecast, aiMatch.Score, aiMatch.BTTSLabel, aiMatch.IsLive);
            }
        }

        // ── Fallback: FlashScore for any predictions still missing a score ──
        var scores = await _dbContext.MatchScores
            .Where(s => s.MatchTime >= startOfWindowUtc && s.MatchTime < endOfWindowUtc)
            .ToListAsync();

        var incompletePredictions = predictionsForSettlement
            .Where(NeedsPredictionSettlementRepair)
            .ToList();
        var incompleteForecasts = forecastsForSettlement
            .Where(f => string.IsNullOrEmpty(f.ActualScore) || f.IsLive || !f.IsSettled)
            .ToList();

        var predLookup = incompletePredictions
            .GroupBy(p => CreateScoreFixtureKey(p.Date, p.HomeTeam, p.AwayTeam, p.League))
            .ToDictionary(g => g.Key, g => g.ToList());
        var forecastLookup = incompleteForecasts
            .GroupBy(f => CreateScoreFixtureKey(f.Date, f.HomeTeam, f.AwayTeam, f.League))
            .ToDictionary(g => g.Key, g => g.ToList());

        if (incompletePredictions.Count > 0 && scores.Count > 0)
        {
            _logger.LogInformation(
                "Attempting fallback score match from FlashScore for {Count} incomplete predictions.",
                incompletePredictions.Count);

            foreach (var score in scores)
            {
                var scoreDate = DateTimeProvider.ConvertUtcToLocal(score.MatchTime).ToString("dd-MM-yyyy");
                var key = CreateScoreFixtureKey(scoreDate, score.HomeTeam, score.AwayTeam, score.League);

                if (!predLookup.TryGetValue(key, out var matched))
                {
                    var bestPrediction = FindBestFixtureCandidate(
                        incompletePredictions,
                        score.HomeTeam,
                        score.AwayTeam,
                        score.League,
                        score.MatchTime,
                        prediction => prediction.HomeTeam,
                        prediction => prediction.AwayTeam,
                        prediction => prediction.League,
                        prediction => ResolveScheduledMatchTime(prediction.Date, prediction.Time, prediction.MatchDateTime));

                    if (bestPrediction == null)
                    {
                        continue;
                    }

                    var bestKey = CreateScoreFixtureKey(
                        bestPrediction.Date,
                        bestPrediction.HomeTeam,
                        bestPrediction.AwayTeam,
                        bestPrediction.League);

                    if (!predLookup.TryGetValue(bestKey, out matched))
                    {
                        matched = [];
                    }
                }

                // Repair anything still missing a score, still marked live, or missing its derived outcome.
                matched = matched.Where(NeedsPredictionSettlementRepair).ToList();
                if (matched.Count == 0) continue;

                foreach (var prediction in matched)
                {
                    UpdatePredictionSettlementState(prediction, score.Score, score.BTTSLabel, score.IsLive);
                }

                if (!forecastLookup.TryGetValue(key, out var matchedForecasts))
                {
                    var bestForecast = FindBestFixtureCandidate(
                        incompleteForecasts,
                        score.HomeTeam,
                        score.AwayTeam,
                        score.League,
                        score.MatchTime,
                        forecast => forecast.HomeTeam,
                        forecast => forecast.AwayTeam,
                        forecast => forecast.League,
                        forecast => ResolveScheduledMatchTime(forecast.Date, forecast.Time, forecast.MatchDateTime));

                    if (bestForecast == null)
                    {
                        continue;
                    }

                    var bestKey = CreateScoreFixtureKey(
                        bestForecast.Date,
                        bestForecast.HomeTeam,
                        bestForecast.AwayTeam,
                        bestForecast.League);

                    if (!forecastLookup.TryGetValue(bestKey, out matchedForecasts))
                    {
                        matchedForecasts = [];
                    }
                }

                matchedForecasts = matchedForecasts
                    .Where(f => string.IsNullOrEmpty(f.ActualScore) || f.IsLive || !f.IsSettled)
                    .ToList();
                if (matchedForecasts.Count == 0) continue;

                foreach (var forecast in matchedForecasts)
                {
                    UpdateForecastObservationState(forecast, score.Score, score.BTTSLabel, score.IsLive);
                }
            }
        }

        // ── Matching Statistics & Diagnostics ──
        var matchedCount = predictionsForSettlement.Count(p => !string.IsNullOrEmpty(p.ActualScore));
        var unmatchedPredictions = predictionsForSettlement
            .Where(p => string.IsNullOrEmpty(p.ActualScore))
            .ToList();

        _logger.LogInformation(
            "📊 Score matching summary: {Matched}/{Total} predictions matched ({Percentage}%) in the {LookbackDays}-day settlement window, {Unmatched} unmatched.",
            matchedCount,
            predictionsForSettlement.Count,
            predictionsForSettlement.Count > 0 ? (matchedCount * 100 / predictionsForSettlement.Count) : 0,
            ScoreUpdaterLookbackDays,
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

        _logger.LogInformation("✅ Predictions updated successfully.");

        await _dbContext.SaveChangesAsync();
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
        var effectiveIsLive = DetermineEffectiveIsLive(
            isLive,
            ResolveScheduledMatchTime(prediction.Date, prediction.Time, prediction.MatchDateTime));

        prediction.ActualScore = score;
        prediction.IsLive = effectiveIsLive;

        if (effectiveIsLive)
        {
            return;
        }

        prediction.ActualOutcome = DeterminePredictionActualOutcome(
            prediction.PredictionCategory,
            score,
            bttsLabel);
    }

    private void RepairPredictionOutcomeFromStoredScore(Prediction prediction)
    {
        var effectiveIsLive = DetermineEffectiveIsLive(
            prediction.IsLive,
            ResolveScheduledMatchTime(prediction.Date, prediction.Time, prediction.MatchDateTime));

        prediction.IsLive = effectiveIsLive;

        if (effectiveIsLive || string.IsNullOrWhiteSpace(prediction.ActualScore) ||
            !IsOutcomeMissing(prediction.ActualOutcome))
        {
            return;
        }

        prediction.ActualOutcome = DeterminePredictionActualOutcome(
            prediction.PredictionCategory,
            prediction.ActualScore,
            null);
    }

    private bool NeedsPredictionSettlementRepair(Prediction prediction)
    {
        return string.IsNullOrWhiteSpace(prediction.ActualScore) ||
               DetermineEffectiveIsLive(
                   prediction.IsLive,
                   ResolveScheduledMatchTime(prediction.Date, prediction.Time, prediction.MatchDateTime)) ||
               (!string.IsNullOrWhiteSpace(prediction.ActualScore) && IsOutcomeMissing(prediction.ActualOutcome));
    }

    private string? DeterminePredictionActualOutcome(string predictionCategory, string score, bool? bttsLabel)
    {
        return predictionCategory switch
        {
            "BothTeamsScore" => DetermineBttsOutcome(score, bttsLabel),
            "Draw" => DetermineDrawOutcome(score),
            "Over2.5Goals" => DetermineOver25Outcome(score),
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

    private static bool DetermineEffectiveIsLive(bool sourceIsLive, DateTime? scheduledMatchTimeUtc)
    {
        if (!sourceIsLive)
        {
            return false;
        }

        return !scheduledMatchTimeUtc.HasValue ||
               DateTime.UtcNow <= scheduledMatchTimeUtc.Value.AddMinutes(200);
    }

    private void UpdateForecastObservationState(ForecastObservation forecast, string score, bool bttsLabel, bool isLive)
    {
        var effectiveIsLive = DetermineEffectiveIsLive(
            isLive,
            ResolveScheduledMatchTime(forecast.Date, forecast.Time, forecast.MatchDateTime));

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
            PredictionMarket.Draw => DetermineDrawOutcome(score),
            PredictionMarket.HomeWin => (DetermineForecastOutcomeOccurred(market, score, bttsLabel) ?? false) ? "Home Win" : "Not Home Win",
            PredictionMarket.AwayWin => (DetermineForecastOutcomeOccurred(market, score, bttsLabel) ?? false) ? "Away Win" : "Not Away Win",
            PredictionMarket.StraightWin => DetermineStraightWinOutcome(score),
            _ => null
        };
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

        // 1. Find the time range of the incoming scores to pull existing records in bulk
        var minTime = scores.Min(s => s.MatchTime);
        var maxTime = scores.Max(s => s.MatchTime);

        // 2. Fetch all potential matches in this time window in ONE query
        var existingScoresList = await _dbContext.MatchScores
            .Where(s => s.MatchTime >= minTime && s.MatchTime <= maxTime)
            .ToListAsync();

        // 3. Create a Dictionary for lighting-fast O(1) memory lookups
        var existingScoresDict = existingScoresList
            .GroupBy(s => (s.HomeTeam, s.AwayTeam, s.MatchTime))
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var incomingScore in scores)
        {
            var key = (incomingScore.HomeTeam, incomingScore.AwayTeam, incomingScore.MatchTime);

            if (existingScoresDict.TryGetValue(key, out var existingRecord))
            {
                // UPDATE SCENARIO: The match exists. 
                // Only update the database if the score or live status actually changed.
                if (existingRecord.Score != incomingScore.Score || 
                    existingRecord.IsLive != incomingScore.IsLive)
                {
                    existingRecord.Score = incomingScore.Score;
                    existingRecord.IsLive = incomingScore.IsLive;
                
                    // Update any other fields that might change during a match
                    existingRecord.BTTSLabel = incomingScore.BTTSLabel; 
                }
            }
            else
            {
                // INSERT SCENARIO: The match does not exist yet.
                _dbContext.MatchScores.Add(incomingScore);
            }
        }

        // 4. Save all inserts and updates in a single transaction
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
                if (existingRecord.Score != incomingScore.Score ||
                    existingRecord.IsLive != incomingScore.IsLive)
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
        var today = DateTimeProvider.GetLocalTime().Date;
        var dates = Enumerable.Range(0, Math.Max(lookbackDays, 0) + 1)
            .Select(offset => today.AddDays(-offset).ToString("dd-MM-yyyy"))
            .ToHashSet(StringComparer.Ordinal);

        var matches = await _dbContext.MatchDatas
            .Where(match => match.Date != null && dates.Contains(match.Date))
            .ToListAsync();

        if (matches.Count == 0)
        {
            return;
        }

        var predictions = await _dbContext.Predictions
            .Where(prediction => dates.Contains(prediction.Date))
            .ToListAsync();

        var forecasts = await _dbContext.ForecastObservations
            .Where(forecast => dates.Contains(forecast.Date))
            .ToListAsync();

        var matchLookup = matches
            .GroupBy(match => (
                Date: match.Date ?? string.Empty,
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
            var matched = FindMatchingMatchData(prediction.Date, prediction.HomeTeam, prediction.AwayTeam, prediction.League, prediction.MatchDateTime, matchLookup, teamLookup);
            if (matched != null && ApplyStoredTime(prediction, matched))
            {
                updatedPredictions++;
            }
        }

        foreach (var forecast in forecasts)
        {
            var matched = FindMatchingMatchData(forecast.Date, forecast.HomeTeam, forecast.AwayTeam, forecast.League, forecast.MatchDateTime, matchLookup, teamLookup);
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

    public async Task BackfillDecisionProvenanceAsync(int lookbackDays = 90)
    {
        var today = DateTimeProvider.GetLocalTime().Date;
        var dates = Enumerable.Range(0, Math.Max(lookbackDays, 0) + 1)
            .Select(offset => today.AddDays(-offset).ToString("dd-MM-yyyy"))
            .ToHashSet(StringComparer.Ordinal);

        var predictions = await _dbContext.Predictions
            .Where(prediction =>
                dates.Contains(prediction.Date) &&
                (string.IsNullOrEmpty(prediction.CalibratorUsed) ||
                 prediction.CalibratorUsed == "Unknown" ||
                 string.IsNullOrEmpty(prediction.ThresholdSource) ||
                 prediction.ThresholdSource == "Unknown" ||
                 prediction.ThresholdUsed <= 0 ||
                 !prediction.WasPublished))
            .ToListAsync();

        var forecasts = await _dbContext.ForecastObservations
            .Where(forecast =>
                dates.Contains(forecast.Date) &&
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
    
    private async Task SavePredictions(IEnumerable<PredictionCandidate> candidates)
    {
        var candidateList = candidates.ToList();
        if (!candidateList.Any()) return;

        // 2. Extract unique dates to fetch existing records in ONE bulk query
        var uniqueDates = candidateList.Select(c => c.Date).Distinct().ToList();

        var existingPredictions = await _dbContext.Predictions
            .Where(p => uniqueDates.Contains(p.Date))
            .ToListAsync();

        // 3. Create a Dictionary for O(1) memory lookups
        var existingDict = existingPredictions
            .GroupBy(p => (
                Home: (p.HomeTeam ?? "").Trim().ToLowerInvariant(), 
                Away: (p.AwayTeam ?? "").Trim().ToLowerInvariant(), 
                League: (p.League ?? "").Trim().ToLowerInvariant(), 
                Date: p.Date,
                Category: p.PredictionCategory))
            .ToDictionary(g => g.Key, g => g.First());

        // 4. Loop through the memory collection, not the database
        foreach (var candidate in candidateList)
        {
            var homeTeam = candidate.HomeTeam ?? "N/A";
            var awayTeam = candidate.AwayTeam ?? "N/A";
            var league = candidate.League ?? "N/A";

            var key = (
                Home: homeTeam.Trim().ToLowerInvariant(),
                Away: awayTeam.Trim().ToLowerInvariant(),
                League: league.Trim().ToLowerInvariant(),
                Date: candidate.Date,
                Category: candidate.PredictionCategory);

            if (existingDict.TryGetValue(key, out var existingRecord))
            {
                // UPDATE SCENARIO

                if (existingRecord.Time != candidate.Time || existingRecord.MatchDateTime != candidate.MatchDateTime)
                {
                    existingRecord.Time = candidate.Time;
                    existingRecord.MatchDateTime = candidate.MatchDateTime;
                }

                if (existingRecord.PredictedOutcome != candidate.PredictedOutcome)
                {
                    existingRecord.PredictedOutcome = candidate.PredictedOutcome;
                }

                existingRecord.RawConfidenceScore = Math.Round((decimal)candidate.RawProbability, 4);
                existingRecord.ConfidenceScore = Math.Round((decimal)candidate.CalibratedProbability, 4);
                existingRecord.CalibratorUsed = candidate.CalibratorUsed;
                existingRecord.ThresholdUsed = Math.Round(candidate.ThresholdUsed, 4);
                existingRecord.ThresholdSource = candidate.ThresholdSource;
                existingRecord.WasPublished = candidate.WasPublished;
            }
            else
            {
                // INSERT SCENARIO
                var prediction = new Prediction
                {
                    HomeTeam = homeTeam.Trim(),
                    AwayTeam = awayTeam.Trim(),
                    League = league.Trim(),
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
                    MatchDateTime = candidate.MatchDateTime
                };

                _dbContext.Predictions.Add(prediction);
                existingDict[key] = prediction;
            }
        }

        // 5. Save all inserts and updates in a single transaction
        await _dbContext.SaveChangesAsync();
    }

    private async Task SaveForecastObservations(
        IEnumerable<PredictionCandidate> forecastCandidates,
        IEnumerable<PredictionCandidate> publishedCandidates)
    {
        var forecastList = forecastCandidates.ToList();
        if (!forecastList.Any()) return;

        var publishedKeys = publishedCandidates
            .Select(GetCandidateObservationKey)
            .ToHashSet(StringComparer.Ordinal);

        var uniqueDates = forecastList.Select(candidate => candidate.Date).Distinct().ToList();
        var existingForecasts = await _dbContext.ForecastObservations
            .Where(forecast => uniqueDates.Contains(forecast.Date))
            .ToListAsync();

        var existingDict = existingForecasts
            .GroupBy(GetObservationKey)
            .ToDictionary(group => group.Key, group => group.First());

        foreach (var candidate in forecastList)
        {
            var key = GetCandidateObservationKey(candidate);
            var isPublished = publishedKeys.Contains(key);

            if (existingDict.TryGetValue(key, out var existingRecord))
            {
                existingRecord.Time = candidate.Time;
                existingRecord.MatchDateTime = candidate.MatchDateTime;
                existingRecord.PredictedOutcome = candidate.PredictedOutcome;
                existingRecord.RawProbability = candidate.RawProbability;
                existingRecord.CalibratedProbability = candidate.CalibratedProbability;
                existingRecord.IsPublished = isPublished;
                existingRecord.CalibratorUsed = candidate.CalibratorUsed;
                existingRecord.ThresholdUsed = Math.Round(candidate.ThresholdUsed, 4);
                existingRecord.ThresholdSource = candidate.ThresholdSource;
            }
            else
            {
                var observation = new ForecastObservation
                {
                    Date = candidate.Date,
                    Time = candidate.Time,
                    MatchDateTime = candidate.MatchDateTime,
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
                    IsPublished = isPublished
                };

                _dbContext.ForecastObservations.Add(observation);
                existingDict[key] = observation;
            }
        }

        await _dbContext.SaveChangesAsync();
    }

    private static string GetObservationKey(ForecastObservation forecast)
    {
        return $"{forecast.Date}|{Norm(forecast.HomeTeam)}|{Norm(forecast.AwayTeam)}|{Norm(forecast.League)}|{forecast.Market}";
    }

    private static string GetCandidateObservationKey(PredictionCandidate candidate)
    {
        return $"{candidate.Date}|{Norm(candidate.HomeTeam)}|{Norm(candidate.AwayTeam)}|{Norm(candidate.League)}|{candidate.Market}";
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
            "Draw" => PredictionMarket.Draw,
            "StraightWin" when prediction.PredictedOutcome == "Home Win" => PredictionMarket.HomeWin,
            "StraightWin" when prediction.PredictedOutcome == "Away Win" => PredictionMarket.AwayWin,
            _ => default
        };

        return prediction.PredictionCategory is "BothTeamsScore" or "Over2.5Goals" or "Draw" ||
               (prediction.PredictionCategory == "StraightWin" && prediction.PredictedOutcome is "Home Win" or "Away Win");
    }

    private double ResolveFallbackThreshold(PredictionMarket market)
    {
        return market switch
        {
            PredictionMarket.BothTeamsScore => _predictionSettings.BttsScoreThreshold,
            PredictionMarket.Over25Goals => _predictionSettings.OverTwoGoalsStrongThreshold,
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

    private static DateTime? ResolveScheduledMatchTime(string? date, string? time, DateTime? matchDateTime)
    {
        if (matchDateTime.HasValue)
        {
            return matchDateTime.Value;
        }

        if (string.IsNullOrWhiteSpace(date) || string.IsNullOrWhiteSpace(time))
        {
            return null;
        }

        return DateTime.TryParseExact(
            $"{date} {time}",
            ["dd-MM-yyyy HH:mm", "dd-MM-yyyy H:mm"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? DateTimeProvider.ConvertLocalToUtc(parsed)
            : null;
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
        IEnumerable<T> candidates,
        string homeTeam,
        string awayTeam,
        string? league,
        DateTime? targetMatchTime,
        Func<T, string> homeSelector,
        Func<T, string> awaySelector,
        Func<T, string?> leagueSelector,
        Func<T, DateTime?> matchTimeSelector)
    {
        var targetHomeKey = ScoreMatchingHelper.CreateTeamLookupKey(homeTeam, league);
        var targetAwayKey = ScoreMatchingHelper.CreateTeamLookupKey(awayTeam, league);

        var exactCandidates = candidates
            .Where(candidate =>
                ScoreMatchingHelper.CreateTeamLookupKey(homeSelector(candidate), leagueSelector(candidate)) == targetHomeKey &&
                ScoreMatchingHelper.CreateTeamLookupKey(awaySelector(candidate), leagueSelector(candidate)) == targetAwayKey)
            .ToList();

        if (exactCandidates.Count == 1)
        {
            return exactCandidates[0];
        }

        return SelectBestFixtureCandidate(
            exactCandidates.Count > 1 ? exactCandidates : candidates,
            homeTeam,
            awayTeam,
            league,
            targetMatchTime,
            homeSelector,
            awaySelector,
            leagueSelector,
            matchTimeSelector);
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
        Func<T, DateTime?> matchTimeSelector)
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
            var totalScore = baseScore + (exactPair ? 0.20 : 0.0) + (leagueScore * 0.15) + (timeScore * 0.10);

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

    private static MatchData? FindMatchingMatchData(
        string date,
        string homeTeam,
        string awayTeam,
        string league,
        DateTime? matchDateTime,
        IReadOnlyDictionary<(string Date, string Home, string Away, string League), MatchData> datedMatches,
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

        return updated;
    }

}
