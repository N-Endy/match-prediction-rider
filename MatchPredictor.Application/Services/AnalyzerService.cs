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
    public async Task RunScraperAndAnalyzerAsync()
    {
        _logger.LogInformation("Starting scraping and analysis process...");

        try
        {
            await _webScraperService.ScrapeMatchDataAsync();
            _logger.LogInformation("✅ Web scraping completed successfully.");

            // Score scraping is non-blocking — predictions should save even if scores fail
            try
            {
                var scores = await _webScraperService.ScrapeMatchScoresAsync();
                _logger.LogInformation("Scraped {Count} match scores from website.", scores.Count);
                await SaveMatchScores(scores);
            }
            catch (Exception scoreEx)
            {
                _logger.LogWarning(scoreEx, "❌ Score scraping failed — continuing with predictions.");
            }

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

            await UpdatePredictionsWithActualResults();
            _logger.LogInformation("✅ Predictions updated with actual results.");

            await AnalyzePatterns();
            _logger.LogInformation("✅ Pattern analysis completed.");

            // Regression-based predictions using historical match scores
            var regressionPredictions = _regressionPredictorService.GeneratePredictions(scraped);
            await SaveRegressionPredictions(regressionPredictions);
            _logger.LogInformation("✅ Regression-based predictions saved successfully.");

            await _dbContext.ScrapingLogs.AddAsync(new ScrapingLog
            {
                Timestamp = DateTime.UtcNow,
                Status = "Success",
                Message = "✅ Scraping and prediction analysis completed successfully."
            });
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation("Scraping log saved successfully.");
        }
        catch (Exception ex)
        {
            var log = new ScrapingLog
            {
                Timestamp = DateTime.UtcNow,
                Status = "Failed",
                Message = $"{ex.Message}"
            };
            _logger.LogError(ex, "❌ An error occurred during scraping and analysis.");

            await _dbContext.ScrapingLogs.AddAsync(log);
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation("Scraping log saved with error status.");
            throw; // Re-throw the exception to ensure Hangfire marks the job as failed
        }
    }

    private async Task UpdatePredictionsWithActualResults()
    {
        var today = DateTimeProvider.GetLocalTime().Date;
        var dateStr = today.ToString("dd-MM-yyyy");

        // 1. Set up the UTC boundaries for the start and end of the day
        var startOfDayUtc = DateTime.SpecifyKind(today, DateTimeKind.Utc);
        var endOfDayUtc = startOfDayUtc.AddDays(1);

        // 2. Use the index-friendly range query to fetch scores
        var scores = await _dbContext.MatchScores
            .Where(s => s.MatchTime >= startOfDayUtc && s.MatchTime < endOfDayUtc)
            .ToListAsync();

        var predictionsForToday = await _dbContext.Predictions
            .Where(p => p.Date == dateStr)
            .ToListAsync();

        // Key = date + normalized teams (you can also include league if needed)
        var predLookup = predictionsForToday
            .GroupBy(p => (p.Date, Home: Norm(p.HomeTeam), Away: Norm(p.AwayTeam)))
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var score in scores)
        {
            var key = (dateStr, Home: Norm(score.HomeTeam), Away: Norm(score.AwayTeam));

            // if you need fuzzy matching, you can fall back to your TeamsMatch logic here
            if (!predLookup.TryGetValue(key, out var matched))
            {
                // fallback fuzzy match (optional)
                matched = predictionsForToday
                    .Where(p =>
                        ScoreMatchingHelper.TeamsMatch(score.HomeTeam, p.HomeTeam) &&
                        ScoreMatchingHelper.TeamsMatch(score.AwayTeam, p.AwayTeam))
                    .ToList();
            }

            switch (matched.Count)
            {
                case 0:
                    continue;
                // optionally filter by league if multiple
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
    }
    

    // private async Task SavePredictions(string category, IEnumerable<MatchData> matches)
    // {
    //     foreach (var match in matches)
    //     {
    //         var properDateTime = DateTimeProvider.ParseProperDateAndTime(match.Date, match.Time);
    //         DateTime? matchDateTime = null;
    //         if (DateTime.TryParseExact(
    //                 $"{properDateTime.date} {properDateTime.time}",
    //                 "dd-MM-yyyy HH:mm",
    //                 System.Globalization.CultureInfo.InvariantCulture,
    //                 System.Globalization.DateTimeStyles.None,
    //                 out var parsed))
    //         {
    //             matchDateTime = parsed;
    //         }
    //         var prediction = new Prediction
    //         {
    //             HomeTeam = match.HomeTeam ?? "N/A",
    //             AwayTeam = match.AwayTeam ?? "N/A",
    //             League = match.League ?? "N/A",
    //             PredictionCategory = category,
    //             PredictedOutcome = category switch
    //             {
    //                 "BothTeamsScore" => "BTTS",
    //                 "Draw" => "Draw",
    //                 "Over2.5Goals" => "Over 2.5",
    //                 "StraightWin" => match.HomeWin > match.AwayWin ? "Home Win" : "Away Win",
    //                 _ => "Unknown"
    //             },
    //             Date = properDateTime.date,
    //             Time = properDateTime.time,
    //             MatchDateTime = matchDateTime
    //         };
    //
    //         var exists = await _dbContext.Predictions.AnyAsync(p =>
    //             p.HomeTeam == prediction.HomeTeam &&
    //             p.AwayTeam == prediction.AwayTeam &&
    //             p.League == prediction.League &&
    //             p.Date == prediction.Date);
    //
    //         if (!exists)
    //         {
    //             _dbContext.Predictions.Add(prediction);
    //         }
    //     }
    //
    //     await _dbContext.SaveChangesAsync();
    // }

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
                bool isUpdated = false;

                if (existingRecord.Time != item.TimeStr)
                {
                    existingRecord.Time = item.TimeStr;
                    existingRecord.MatchDateTime = item.MatchDateTime;
                    isUpdated = true;
                }

                if (existingRecord.PredictedOutcome != predictedOutcome)
                {
                    existingRecord.PredictedOutcome = predictedOutcome;
                    isUpdated = true;
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

