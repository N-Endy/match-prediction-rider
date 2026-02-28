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
    public async Task RunPredictionGenerationAsync()
    {
        _logger.LogInformation("Starting prediction generation process (every 3h)...");
        try
        {
            await _webScraperService.ScrapeMatchDataAsync();
            _logger.LogInformation("✅ Web scraping completed successfully.");

            var scraped = _excelExtract.ExtractMatchDatasetFromFile().ToList();
            try
            {
                // Check if they exist in the database first, and only put those not existing.
                foreach (var match in scraped)
                {
                    var properDateTime = DateTimeProvider.ParseProperDateAndTime(match.Date, match.Time);
                    match.Date = properDateTime.date;
                    match.Time = properDateTime.time;

                    if (DateTime.TryParseExact(
                            $"{properDateTime.date} {properDateTime.time}",
                            "dd-MM-yyyy HH:mm",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None,
                            out var parsed))
                    {
                        match.MatchDateTime = parsed;
                    }

                    var exists = await _dbContext.MatchDatas.AnyAsync(m =>
                        m.HomeTeam == match.HomeTeam &&
                        m.AwayTeam == match.AwayTeam &&
                        m.League == match.League &&
                        m.Date == match.Date &&
                        m.Time == match.Time);
                    
                    if (!exists) await _dbContext.MatchDatas.AddAsync(match);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "❌ Failed to save match data to database.");
                throw;
            }
            _logger.LogInformation($"Extracted {scraped.Count} matches from Excel file.");
            
            var matches = await _dbContext.MatchDatas.ToListAsync();

            await SavePredictions("BothTeamsScore", _dataAnalyzerService.BothTeamsScore(matches));
            await SavePredictions("Draw", _dataAnalyzerService.Draw(matches));
            await SavePredictions("Over2.5Goals", _dataAnalyzerService.OverTwoGoals(matches));
            await SavePredictions("StraightWin", _dataAnalyzerService.StraightWin(matches));
            
            _logger.LogInformation("✅ Predictions saved successfully.");

            await LogScrapingStatus("Success", "✅ Prediction generation completed successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ An error occurred during prediction generation.");
            await LogScrapingStatus("Failed", $"Prediction Gen Error: {ex.Message}");
            throw;
        }
    }

    [AutomaticRetry(OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public async Task RunScoreUpdaterAsync()
    {
        _logger.LogInformation("Starting score updating process (every 15m)...");
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
        _logger.LogInformation("Starting daily analysis process (midnight)...");
        try
        {
            await AnalyzePatterns();
            _logger.LogInformation("✅ Pattern analysis completed.");

            var scraped = _excelExtract.ExtractMatchDatasetFromFile().ToList();
            var regressionPredictions = _regressionPredictorService.GeneratePredictions(scraped);
            await SaveRegressionPredictions(regressionPredictions);
            _logger.LogInformation("✅ Regression-based predictions saved successfully.");

            await LogScrapingStatus("Success", "✅ Daily analysis completed successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ An error occurred during daily analysis.");
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
            }
        }

        // ── Fallback: FlashScore for any predictions still missing a score ──
        var scores = await _dbContext.MatchScores
            .Where(s => s.MatchTime >= startOfDayUtc && s.MatchTime < endOfDayUtc)
            .ToListAsync();

        var missingScorePredictions = predictionsForToday
            .Where(p => string.IsNullOrEmpty(p.ActualScore))
            .ToList();

        var predLookup = missingScorePredictions
            .GroupBy(p => (p.Date, Home: Norm(p.HomeTeam), Away: Norm(p.AwayTeam)))
            .ToDictionary(g => g.Key, g => g.ToList());

        if (missingScorePredictions.Count > 0 && scores.Count > 0)
        {
            _logger.LogInformation(
                "Attempting fallback score match from FlashScore for {Count} predictions.",
                missingScorePredictions.Count);

            foreach (var score in scores)
            {
                var key = (dateStr, Home: Norm(score.HomeTeam), Away: Norm(score.AwayTeam));

                if (!predLookup.TryGetValue(key, out var matched))
                {
                    matched = missingScorePredictions
                        .Where(p =>
                            ScoreMatchingHelper.TeamsMatch(score.HomeTeam, p.HomeTeam) &&
                            ScoreMatchingHelper.TeamsMatch(score.AwayTeam, p.AwayTeam))
                        .ToList();
                }

                // Only update predictions that don't already have a score (from AiScore)
                matched = matched.Where(p => string.IsNullOrEmpty(p.ActualScore)).ToList();

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
                }
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
        var allPredictions = await _dbContext.Predictions
            .Where(p => p.ActualOutcome != null)
            .ToListAsync();
        
        var categoryGroups = allPredictions
            .GroupBy(p => p.PredictionCategory)
            .Select(g => new 
            {
                Category = g.Key,
                Total = g.Count(),
                Correct = g.Count(p => p.PredictedOutcome == p.ActualOutcome)
            })
            .ToList();
        
        foreach (var group in categoryGroups)
        {
            var accuracy = group.Total > 0 ? (double)group.Correct / group.Total : 0;
            _logger.LogInformation($"Category {group.Category}: {accuracy:P2} accuracy ({group.Correct}/{group.Total})");
        }
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

