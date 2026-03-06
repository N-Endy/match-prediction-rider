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
    
    public AnalyzerService(
        IDataAnalyzerService dataAnalyzerService,
        IWebScraperService webScraperService,
        ApplicationDbContext dbContext,
        IExtractFromExcel excelExtract,
        IRegressionPredictorService regressionPredictorService,
        ILogger<AnalyzerService> logger)
    {
        _dataAnalyzerService = dataAnalyzerService;
        _webScraperService = webScraperService;
        _dbContext = dbContext;
        _excelExtract = excelExtract;
        _regressionPredictorService = regressionPredictorService;
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
                // Check if they exist in the database first, and only put those not existing.
                foreach (var match in scraped)
                {
                    var properDateTime = DateTimeProvider.ParseProperDateAndTime(match.Date, match.Time);
                    match.Date = properDateTime.date;
                    match.Time = properDateTime.time;
                    match.MatchDateTime = properDateTime.utcDateTime;

                    var exists = await _dbContext.MatchDatas.AnyAsync(m =>
                        m.HomeTeam == match.HomeTeam &&
                        m.AwayTeam == match.AwayTeam &&
                        m.League == match.League &&
                        m.Date == match.Date &&
                        m.Time == match.Time);
                    
                    if (!exists) await _dbContext.MatchDatas.AddAsync(match);
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
            var matches = await _dbContext.MatchDatas.ToListAsync();
            var accuracies = await _dbContext.ModelAccuracies.ToListAsync();

            await SavePredictions("BothTeamsScore", _dataAnalyzerService.BothTeamsScore(matches, accuracies));
            await SavePredictions("Draw", _dataAnalyzerService.Draw(matches, accuracies));
            await SavePredictions("Over2.5Goals", _dataAnalyzerService.OverTwoGoals(matches, accuracies));
            await SavePredictions("StraightWin", _dataAnalyzerService.StraightWin(matches, accuracies));
            
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

        // ── Step 1: Pattern Analysis (independent) ──
        try
        {
            await AnalyzePatterns();
            _logger.LogInformation("✅ Pattern analysis completed.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Pattern analysis failed, continuing with regression predictions.");
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
                scraped = await _dbContext.MatchDatas.ToListAsync();
            }

            if (scraped.Count == 0)
            {
                _logger.LogWarning("⚠️ No match data available for regression predictions. Skipping.");
            }
            else
            {
                var accuracies = await _dbContext.ModelAccuracies.ToListAsync();
                var regressionPredictions = _regressionPredictorService.GeneratePredictions(scraped, accuracies);
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

                prediction.ActualOutcome = prediction.PredictionCategory switch
                {
                    "BothTeamsScore" => aiMatch.BTTSLabel ? "BTTS" : "No BTTS",
                    "Draw"           => DetermineDrawOutcome(aiMatch.Score),
                    "Over2.5Goals"   => DetermineOver25Outcome(aiMatch.Score),
                    "StraightWin"    => DetermineStraightWinOutcome(aiMatch.Score),
                    _                => prediction.ActualOutcome
                };

                prediction.ActualScore = aiMatch.Score;
                prediction.IsLive = aiMatch.IsLive;
                prediction.HasStream = aiMatch.HasStream;
                prediction.AiScoreMatchId = aiMatch.AiScoreMatchId;
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
                    prediction.ActualOutcome = prediction.PredictionCategory switch
                    {
                        "BothTeamsScore" => score.BTTSLabel ? "BTTS" : "No BTTS",
                        "Draw"           => DetermineDrawOutcome(score.Score),
                        "Over2.5Goals"   => DetermineOver25Outcome(score.Score),
                        "StraightWin"    => DetermineStraightWinOutcome(score.Score),
                        _                => prediction.ActualOutcome
                    };

                    prediction.ActualScore = score.Score;
                    prediction.IsLive = score.IsLive;
                    prediction.HasStream = score.HasStream;
                    prediction.AiScoreMatchId = score.AiScoreMatchId;
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

    
    private async Task AnalyzePatterns()
    {
        _logger.LogInformation("Starting detailed pattern analysis for self-learning...");
        
        // 1. Fetch completed predictions with their matching MatchData
        var completedPredictions = await _dbContext.Predictions
            .Where(p => p.ActualOutcome != null)
            .ToListAsync();
            
        var allMatchData = await _dbContext.MatchDatas.ToListAsync();
        
        var predictionDataPairs = completedPredictions
            .Join(allMatchData,
                p => new { p.Date, p.HomeTeam, p.AwayTeam }!,
                m => new { m.Date, m.HomeTeam, m.AwayTeam },
                (p, m) => new { Prediction = p, Match = m })
            .ToList();

        if (predictionDataPairs.Count == 0)
        {
            _logger.LogInformation("No completed predictions with match data found to analyze.");
            return;
        }

        var newAccuracies = new List<ModelAccuracy>();
        var now = DateTime.UtcNow;

        // 2. Define the metrics we want to track (including secondary fallbacks)
        var metricsToTrack = new List<(string Name, Func<MatchData, double> Selector)>
        {
            // Primary Signals
            ("AhMinusHalfHome", m => m.AhMinusHalfHome),
            ("AhPlusHalfAway", m => m.AhPlusHalfAway),
            ("OverTwoGoals", m => m.OverTwoGoals),
            ("HomeWin", m => m.HomeWin),
            ("AwayWin", m => m.AwayWin),
            
            // Secondary/Fallback Signals (for when primary is 0)
            ("AhMinusOneHome", m => m.AhMinusOneHome),
            ("AhPlusHalfHome", m => m.AhPlusHalfHome), // Away win fallback correlation
            ("OverOnePointFive", m => m.OverOnePointFive),
            ("OverThreeGoals", m => m.OverThreeGoals),
            ("DrawOdds", m => m.Draw)
        };

        // 3. Bucketize & Aggregate
        foreach (var categoryGroup in predictionDataPairs.GroupBy(x => x.Prediction.PredictionCategory))
        {
            var category = categoryGroup.Key;

            foreach (var metric in metricsToTrack)
            {
                // Create buckets of 0.10 ranges (e.g., 0.50 to 0.59)
                // Filter out 0 values as they typically represent missing data in MatchData
                var bucketed = categoryGroup
                    .Where(x => metric.Selector(x.Match) > 0)
                    .GroupBy(x => 
                    {
                        var val = metric.Selector(x.Match);
                        return Math.Floor(val * 10) / 10.0; 
                    });

                foreach (var bucket in bucketed)
                {
                    var rangeStart = bucket.Key;
                    var rangeEnd = rangeStart + 0.10;
                    
                    var total = bucket.Count();
                    // Don't save noise. Require at least slightly significant data points.
                    if (total < 5) continue; 

                    var correct = bucket.Count(x => x.Prediction.PredictedOutcome == x.Prediction.ActualOutcome);
                    var accuracy = (double)correct / total;

                    newAccuracies.Add(new ModelAccuracy
                    {
                        Category = category,
                        MetricName = metric.Name,
                        MetricRangeStart = rangeStart,
                        MetricRangeEnd = rangeEnd,
                        TotalPredictions = total,
                        CorrectPredictions = correct,
                        AccuracyPercentage = accuracy,
                        LastUpdated = now
                    });
                }
            }
        }

        // 4. Update the database (Wipe & Replace strategy for simplicity)
        await _dbContext.ModelAccuracies.ExecuteDeleteAsync();
        await _dbContext.ModelAccuracies.AddRangeAsync(newAccuracies);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation($"Saved {newAccuracies.Count} granular accuracy metrics to the database.");
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
                    existingRecord.IsLive != incomingScore.IsLive ||
                    existingRecord.HasStream != incomingScore.HasStream)
                {
                    existingRecord.Score = incomingScore.Score;
                    existingRecord.IsLive = incomingScore.IsLive;
                    existingRecord.HasStream= incomingScore.HasStream;
                    existingRecord.AiScoreMatchId = incomingScore.AiScoreMatchId;
                
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
                    existingRecord.IsLive != incomingScore.IsLive ||
                    existingRecord.HasStream != incomingScore.HasStream)
                {
                    existingRecord.Score = incomingScore.Score;
                    existingRecord.IsLive = incomingScore.IsLive;
                    existingRecord.HasStream= incomingScore.HasStream;
                    existingRecord.AiScoreMatchId = incomingScore.AiScoreMatchId;
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
        var cutoffDate = DateTimeProvider.GetLocalTime().AddDays(-8).Date;
    
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
    
        // Cleanup old MatchScores (primary source)
        await _dbContext.MatchScores
            .Where(s => s.MatchTime < cutoffUtc)
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
    
    private async Task SavePredictions(string category, IEnumerable<MatchData> matches)
    {
        var matchList = matches.ToList();
        if (!matchList.Any()) return;

        // 1. Pre-process the incoming matches to get their parsed dates and times
        var processedMatches = matchList.Select(match =>
        {
            var properDateTime = DateTimeProvider.ParseProperDateAndTime(match.Date, match.Time);
            DateTime? matchDateTime = null;
            
            if (DateTime.TryParseExact(
                    $"{properDateTime.date} {properDateTime.time}",
                    "dd-MM-yyyy HH:mm",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var parsed))
            {
                matchDateTime = parsed;
            }

            return new 
            { 
                Original = match, 
                DateStr = properDateTime.date, 
                TimeStr = properDateTime.time, 
                MatchDateTime = matchDateTime 
            };
        }).ToList();

        // 2. Extract unique dates to fetch existing records in ONE bulk query
        var uniqueDates = processedMatches.Select(m => m.DateStr).Distinct().ToList();

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
        foreach (var item in processedMatches)
        {
            var match = item.Original;
            var homeTeam = match.HomeTeam ?? "N/A";
            var awayTeam = match.AwayTeam ?? "N/A";
            var league = match.League ?? "N/A";

            var predictedOutcome = category switch
            {
                "BothTeamsScore" => "BTTS",
                "Draw"           => "Draw",
                "Over2.5Goals"   => "Over 2.5",
                "StraightWin"    => match.HomeWin > match.AwayWin ? "Home Win" : "Away Win",
                _                => "Unknown"
            };

            var key = (
                Home: homeTeam.Trim().ToLowerInvariant(),
                Away: awayTeam.Trim().ToLowerInvariant(),
                League: league.Trim().ToLowerInvariant(),
                Date: item.DateStr,
                Category: category);

            if (existingDict.TryGetValue(key, out var existingRecord))
            {
                // UPDATE SCENARIO

                if (existingRecord.Time != item.TimeStr)
                {
                    existingRecord.Time = item.TimeStr;
                    existingRecord.MatchDateTime = item.MatchDateTime;
                }

                if (existingRecord.PredictedOutcome != predictedOutcome)
                {
                    existingRecord.PredictedOutcome = predictedOutcome;
                }
            }
            else
            {
                // INSERT SCENARIO
                var prediction = new Prediction
                {
                    HomeTeam = homeTeam.Trim(),
                    AwayTeam = awayTeam.Trim(),
                    League = league.Trim(),
                    PredictionCategory = category,
                    PredictedOutcome = predictedOutcome,
                    Date = item.DateStr,
                    Time = item.TimeStr,
                    MatchDateTime = item.MatchDateTime
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
                // If regression models produce updated probabilities or metrics on a second run, 
                // update those specific fields here. For example:
                
                existingRecord.PredictedOutcome = prediction.PredictedOutcome;
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

