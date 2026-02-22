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
            _logger.LogInformation("Web scraping completed successfully.");

            // Score scraping is non-blocking — predictions should save even if scores fail
            try
            {
                var scores = await _webScraperService.ScrapeMatchScoresAsync();
                _logger.LogInformation("Scraped {Count} match scores from website.", scores.Count);
                await SaveMatchScores(scores);
            }
            catch (Exception scoreEx)
            {
                _logger.LogWarning(scoreEx, "Score scraping failed — continuing with predictions.");
            }

            var scraped = _excelExtract.ExtractMatchDatasetFromFile().ToList();
            try
            {
                // Check if they exist in the database first, and only put those not existing.
                foreach (var match in scraped)
                {
                    var properDateTime = DateTimeProvider.ParseProperDateAndTime(match.Date, match.Time);

                    var exists = await _dbContext.MatchDatas.AnyAsync(m =>
                        m.HomeTeam == match.HomeTeam &&
                        m.AwayTeam == match.AwayTeam &&
                        m.League == match.League &&
                        m.Date == properDateTime.date &&
                        m.Time == properDateTime.time);
                    
                    if (!exists) await _dbContext.MatchDatas.AddAsync(match);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to save match data to database.");
                throw;
            }
            _logger.LogInformation($"Extracted {scraped.Count} matches from Excel file.");
            
            var matches = await _dbContext.MatchDatas.ToListAsync();

            await SavePredictions("BothTeamsScore", _dataAnalyzerService.BothTeamsScore(matches));
            await SavePredictions("Draw", _dataAnalyzerService.Draw(matches));
            await SavePredictions("Over2.5Goals", _dataAnalyzerService.OverTwoGoals(matches));
            await SavePredictions("StraightWin", _dataAnalyzerService.StraightWin(matches));
            
            _logger.LogInformation("Predictions saved successfully.");

            await UpdatePredictionsWithActualResults();
            _logger.LogInformation("Predictions updated with actual results.");

            await AnalyzePatterns();
            _logger.LogInformation("Pattern analysis completed.");

            // Regression-based predictions using historical match scores
            var regressionPredictions = _regressionPredictorService.GeneratePredictions(scraped);
            await SaveRegressionPredictions(regressionPredictions);
            _logger.LogInformation("Regression-based predictions saved successfully.");

            await _dbContext.ScrapingLogs.AddAsync(new ScrapingLog
            {
                Timestamp = DateTime.UtcNow,
                Status = "Success",
                Message = "Scraping and prediction analysis completed successfully."
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
            _logger.LogError(ex, "An error occurred during scraping and analysis.");

            await _dbContext.ScrapingLogs.AddAsync(log);
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation("Scraping log saved with error status.");
            throw; // Re-throw the exception to ensure Hangfire marks the job as failed
        }
    }

    // private async Task UpdatePredictionsWithActualResults()
    // {
    //     var today = DateTimeProvider.GetLocalTime().Date;
    //     var scores = await _dbContext.MatchScores
    //         .Where(s => s.MatchTime.Date == today)
    //         .ToListAsync();
    //
    //     _logger.LogInformation("Score matching: {ScoreCount} scores for date {Date} (UTC: {Utc})",
    //         scores.Count, today.ToString("dd-MM-yyyy"), DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm"));
    //
    //     foreach (var score in scores)
    //     {
    //         var dateStr = score.MatchTime.ToString("dd-MM-yyyy");
    //
    //         // Step 1: Query all predictions for the same date (no time constraint)
    //         var candidates = await _dbContext.Predictions
    //             .Where(p => p.Date == dateStr)
    //             .ToListAsync();
    //
    //         _logger.LogInformation(
    //             "Score: {Home} vs {Away} (date={DateStr}) → {CandidateCount} prediction candidates",
    //             score.HomeTeam, score.AwayTeam, dateStr, candidates.Count);
    //
    //         // Step 2: Match by word-overlap on team names
    //         var matched = candidates
    //             .Where(p =>
    //                 ScoreMatchingHelper.TeamsMatch(score.HomeTeam, p.HomeTeam) &&
    //                 ScoreMatchingHelper.TeamsMatch(score.AwayTeam, p.AwayTeam))
    //             .ToList();
    //
    //         // Step 3: If multiple candidates, prefer those whose league partially matches
    //         if (matched.Count > 1 && !string.IsNullOrWhiteSpace(score.League))
    //         {
    //             var leagueFiltered = matched
    //                 .Where(p => ScoreMatchingHelper.LeaguesMatch(score.League, p.League))
    //                 .ToList();
    //
    //             if (leagueFiltered.Count > 0)
    //                 matched = leagueFiltered;
    //         }
    //
    //         if (matched.Count == 0)
    //         {
    //             _logger.LogInformation("  → NO MATCH. Score teams: [{Home}] vs [{Away}]",
    //                 score.HomeTeam, score.AwayTeam);
    //         }
    //         else
    //         {
    //             _logger.LogInformation("MATCH FOUND: {Home} vs {Away} ({League}) → {MatchCount} prediction(s)",
    //                 score.HomeTeam, score.AwayTeam, score.League, matched.Count);
    //         }
    //
    //         foreach (var prediction in matched)
    //         {
    //             prediction.ActualOutcome = prediction.PredictionCategory switch
    //             {
    //                 "BothTeamsScore" => score.BTTSLabel ? "BTTS" : "No BTTS",
    //                 "Draw" => DetermineDrawOutcome(score.Score),
    //                 "Over2.5Goals" => DetermineOver25Outcome(score.Score),
    //                 "StraightWin" => DetermineStraightWinOutcome(score.Score),
    //                 _ => prediction.ActualOutcome
    //             };
    //             prediction.ActualScore = score.Score;
    //             prediction.IsLive = score.IsLive;
    //             
    //             _logger.LogDebug("Updated prediction for score: {Home} vs {Away} ({League}, {Date}): {Outcome} (Live: {IsLive})",
    //                 score.HomeTeam, score.AwayTeam, score.League, dateStr, prediction.ActualOutcome, score.IsLive);
    //         }
    //     }
    //     
    //     _logger.LogInformation("Predictions updated successfully.");
    //     await _dbContext.SaveChangesAsync();
    // }

    private async Task UpdatePredictionsWithActualResults()
    {
        var today = DateTimeProvider.GetLocalTime().Date;
        var dateStr = today.ToString("dd-MM-yyyy");

        var scores = await _dbContext.MatchScores
            .Where(s => s.MatchTime.Date == today)
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
        
        _logger.LogInformation("Predictions updated successfully.");

        await _dbContext.SaveChangesAsync();
        return;

        static string Norm(string? s) =>
            (s ?? "").Trim().ToLowerInvariant();
    }
    
    // private string DetermineDrawOutcome(string score)
    // {
    //     var parts = score.Split(':');
    //     if (parts.Length != 2) return "Unknown";
    //     
    //     if (int.TryParse(parts[0], out var home) && int.TryParse(parts[1], out var away))
    //     {
    //         return home == away ? "Draw" : "Not Draw";
    //     }
    //     return "Unknown";
    // }
    //
    // private string DetermineOver25Outcome(string score)
    // {
    //     var parts = score.Split(':');
    //     if (parts.Length != 2) return "Unknown";
    //     
    //     if (int.TryParse(parts[0], out var home) && int.TryParse(parts[1], out var away))
    //     {
    //         return (home + away) > 2 ? "Over 2.5" : "Under 2.5";
    //     }
    //     return "Unknown";
    // }
    //
    // private string DetermineStraightWinOutcome(string score)
    // {
    //     var parts = score.Split(':');
    //     if (parts.Length != 2) return "Unknown";
    //     
    //     if (int.TryParse(parts[0], out var home) && int.TryParse(parts[1], out var away))
    //     {
    //         if (home > away) return "Home Win";
    //         return home < away ? "Away Win" : "Draw";
    //     }
    //     return "Unknown";
    // }

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
        foreach (var score in scores)
        {
            var exists = await _dbContext.MatchScores.AnyAsync(s =>
                s.HomeTeam == score.HomeTeam &&
                s.AwayTeam == score.AwayTeam &&
                s.MatchTime == score.MatchTime);

            if (!exists)
            {
                _dbContext.MatchScores.Add(score);
            }
        }
        await _dbContext.SaveChangesAsync();
    }

    
    [AutomaticRetry(OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public async Task CleanupOldPredictionsAndMatchDataAsync()
    {
        var cutoff = DateTimeProvider.GetLocalTime().AddDays(-1);

        var oldPredictions = (await _dbContext.Predictions.ToListAsync())
            .Where(p => DateTime.Parse(p.Date) < cutoff)
            .ToList();
        
        var oldMatchData = (await _dbContext.MatchDatas.ToListAsync())
            .Where(m => DateTime.Parse(m.Date) < cutoff)
            .ToList();

        if (oldPredictions.Count > 0)
            _dbContext.Predictions.RemoveRange(oldPredictions);
        
        if (oldMatchData.Count > 0)
            _dbContext.Predictions.RemoveRange(oldPredictions);
        
        await _dbContext.SaveChangesAsync();
    }

    private async Task SavePredictions(string category, IEnumerable<MatchData> matches)
    {
        foreach (var match in matches)
        {
            var properDateTime = DateTimeProvider.ParseProperDateAndTime(match.Date, match.Time);
            var prediction = new Prediction
            {
                HomeTeam = match.HomeTeam,
                AwayTeam = match.AwayTeam,
                League = match.League,
                PredictionCategory = category,
                PredictedOutcome = category switch
                {
                    "BothTeamsScore" => "BTTS",
                    "Draw" => "Draw",
                    "Over2.5Goals" => "Over 2.5",
                    "StraightWin" => match.HomeWin > match.AwayWin ? "Home Win" : "Away Win",
                    _ => "Unknown"
                },
                Date = properDateTime.date,
                Time = properDateTime.time,
            };

            var exists = await _dbContext.Predictions.AnyAsync(p =>
                p.HomeTeam == prediction.HomeTeam &&
                p.AwayTeam == prediction.AwayTeam &&
                p.League == prediction.League &&
                p.Date == prediction.Date);

            if (!exists)
            {
                _dbContext.Predictions.Add(prediction);
            }
        }

        await _dbContext.SaveChangesAsync();
    }

    private async Task SaveRegressionPredictions(IEnumerable<RegressionPrediction> predictions)
    {
        foreach (var prediction in predictions)
        {
            var exists = await _dbContext.RegressionPredictions.AnyAsync(p =>
                p.HomeTeam == prediction.HomeTeam &&
                p.AwayTeam == prediction.AwayTeam &&
                p.League == prediction.League &&
                p.Date == prediction.Date &&
                p.Time == prediction.Time &&
                p.PredictionCategory == prediction.PredictionCategory);

            if (!exists)
                _dbContext.RegressionPredictions.Add(prediction);
        }

        await _dbContext.SaveChangesAsync();
    }

}

