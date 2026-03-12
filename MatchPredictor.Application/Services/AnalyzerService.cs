using Hangfire;
using MatchPredictor.Application.Helpers;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatchPredictor.Application.Services;

public class AnalyzerService  : IAnalyzerService
{
    private readonly IDataAnalyzerService _dataAnalyzerService;
    private readonly IWebScraperService _webScraperService;
    private readonly ApplicationDbContext _dbContext;
    private readonly IExtractFromExcel _excelExtract;
    private readonly ILogger<AnalyzerService> _logger;
    private readonly IRegressionPredictorService _regressionPredictorService;
    private readonly ICalibrationService _calibrationService;
    
    public AnalyzerService(
        IDataAnalyzerService dataAnalyzerService,
        IWebScraperService webScraperService,
        ApplicationDbContext dbContext,
        IExtractFromExcel excelExtract,
        IRegressionPredictorService regressionPredictorService,
        ICalibrationService calibrationService,
        ILogger<AnalyzerService> logger)
    {
        _dataAnalyzerService = dataAnalyzerService;
        _webScraperService = webScraperService;
        _dbContext = dbContext;
        _excelExtract = excelExtract;
        _regressionPredictorService = regressionPredictorService;
        _calibrationService = calibrationService;
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
            var todayStr = DateTimeProvider.GetLocalTime().ToString("dd-MM-yyyy");
            var matches = await _dbContext.MatchDatas
                .Where(match => match.Date == todayStr)
                .ToListAsync();

            await SavePredictions(_dataAnalyzerService.BothTeamsScore(matches));
            await SavePredictions(_dataAnalyzerService.Draw(matches));
            await SavePredictions(_dataAnalyzerService.OverTwoGoals(matches));
            await SavePredictions(_dataAnalyzerService.StraightWin(matches));
            
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
        var dateStr = today.ToString("dd-MM-yyyy");

        // 1. Set up the UTC boundaries for the start and end of the day
        var startOfDayUtc = DateTime.SpecifyKind(today, DateTimeKind.Utc);
        var endOfDayUtc = startOfDayUtc.AddDays(1);

        // ── Primary: AiScore (broader league coverage) ──
        var predictionsForToday = await _dbContext.Predictions
            .Where(p => p.Date == dateStr)
            .ToListAsync();

        var aiScores = await _dbContext.AiScoreMatchScores
            .Where(s => s.MatchTime >= startOfDayUtc && s.MatchTime < endOfDayUtc)
            .ToListAsync();

        if (aiScores.Count > 0)
        {
            _logger.LogInformation("Matching scores from AiScore ({Count} scores) against {PredCount} predictions.",
                aiScores.Count, predictionsForToday.Count);

            foreach (var prediction in predictionsForToday)
            {
                var aiMatch = aiScores.FirstOrDefault(s =>
                    ScoreMatchingHelper.TeamsMatch(s.HomeTeam, prediction.HomeTeam) &&
                    ScoreMatchingHelper.TeamsMatch(s.AwayTeam, prediction.AwayTeam));

                if (aiMatch == null) continue;

                prediction.ActualScore = aiMatch.Score;
                prediction.IsLive = aiMatch.IsLive;

                if (!aiMatch.IsLive)
                {
                    prediction.ActualOutcome = prediction.PredictionCategory switch
                    {
                        "BothTeamsScore" => aiMatch.BTTSLabel ? "BTTS" : "No BTTS",
                        "Draw"           => DetermineDrawOutcome(aiMatch.Score),
                        "Over2.5Goals"   => DetermineOver25Outcome(aiMatch.Score),
                        "StraightWin"    => DetermineStraightWinOutcome(aiMatch.Score),
                        _                => prediction.ActualOutcome
                    };
                }
            }
        }

        // ── Fallback: FlashScore for any predictions still missing a score ──
        var scores = await _dbContext.MatchScores
            .Where(s => s.MatchTime >= startOfDayUtc && s.MatchTime < endOfDayUtc)
            .ToListAsync();

        var incompletePredictions = predictionsForToday
            .Where(p => string.IsNullOrEmpty(p.ActualScore) || p.IsLive)
            .ToList();

        var predLookup = incompletePredictions
            .GroupBy(p => (p.Date, Home: Norm(p.HomeTeam), Away: Norm(p.AwayTeam)))
            .ToDictionary(g => g.Key, g => g.ToList());

        if (incompletePredictions.Count > 0 && scores.Count > 0)
        {
            _logger.LogInformation(
                "Attempting fallback score match from FlashScore for {Count} incomplete predictions.",
                incompletePredictions.Count);

            foreach (var score in scores)
            {
                var key = (dateStr, Home: Norm(score.HomeTeam), Away: Norm(score.AwayTeam));

                if (!predLookup.TryGetValue(key, out var matched))
                {
                    matched = incompletePredictions
                        .Where(p =>
                            ScoreMatchingHelper.TeamsMatch(score.HomeTeam, p.HomeTeam) &&
                            ScoreMatchingHelper.TeamsMatch(score.AwayTeam, p.AwayTeam))
                        .ToList();
                }

                // Only update predictions that don't already have a score or are still live (from AiScore)
                matched = matched.Where(p => string.IsNullOrEmpty(p.ActualScore) || p.IsLive).ToList();

                switch (matched.Count)
                {
                    case 0:
                        continue;
                    case > 1 when !string.IsNullOrWhiteSpace(score.League):
                    {
                        var leagueFiltered = matched
                            .Where(p => ScoreMatchingHelper.LeaguesMatch(score.League, p.League))
                            .ToList();

                        if (leagueFiltered.Count > 0)
                            matched = leagueFiltered;
                        break;
                    }
                }

                foreach (var prediction in matched)
                {
                    prediction.ActualScore = score.Score;
                    prediction.IsLive = score.IsLive;

                    if (!score.IsLive)
                    {
                        prediction.ActualOutcome = prediction.PredictionCategory switch
                        {
                            "BothTeamsScore" => score.BTTSLabel ? "BTTS" : "No BTTS",
                            "Draw"           => DetermineDrawOutcome(score.Score),
                            "Over2.5Goals"   => DetermineOver25Outcome(score.Score),
                            "StraightWin"    => DetermineStraightWinOutcome(score.Score),
                            _                => prediction.ActualOutcome
                        };
                    }
                }
            }
        }

        // ── Matching Statistics & Diagnostics ──
        var matchedCount = predictionsForToday.Count(p => !string.IsNullOrEmpty(p.ActualScore));
        var unmatchedPredictions = predictionsForToday
            .Where(p => string.IsNullOrEmpty(p.ActualScore))
            .ToList();

        _logger.LogInformation(
            "📊 Score matching summary: {Matched}/{Total} predictions matched ({Percentage}%), {Unmatched} unmatched.",
            matchedCount, predictionsForToday.Count,
            predictionsForToday.Count > 0 ? (matchedCount * 100 / predictionsForToday.Count) : 0,
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
        return;

        static string Norm(string? s) =>
            (s ?? "").Trim().ToLowerInvariant();
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

    
    private async Task RebuildCalibrationProfiles()
    {
        _logger.LogInformation("Starting market calibration rebuild...");
        await _calibrationService.RebuildProfilesAsync();
        var profileCount = await _dbContext.MarketCalibrationProfiles.CountAsync();
        _logger.LogInformation("Updated {Count} calibration profiles.", profileCount);
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

                if (existingRecord.Time != candidate.Time)
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

}
