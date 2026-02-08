using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Hangfire;
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

            var scores = await _webScraperService.ScrapeMatchScoresAsync();
            _logger.LogInformation("Web scraping for scores completed successfully.");
            await SaveMatchScores(scores);

            var scraped = _excelExtract.ExtractMatchDatasetFromFile().ToList();
            _logger.LogInformation($"Extracted {scraped.Count} matches from Excel file.");

            var today = DateTimeProvider.GetLocalTimeString();
            var existingPredictions = await _dbContext.Predictions.Where(p => p.Date == today).ToListAsync();
            _dbContext.Predictions.RemoveRange(existingPredictions);

            var todayScoreDate = DateTimeProvider.GetLocalTime();
            var existingScores = await _dbContext.MatchScores.Where(s => s.MatchTime == todayScoreDate.ToUniversalTime().AddHours(1)).ToListAsync();
            _dbContext.MatchScores.RemoveRange(existingScores);
            await _dbContext.SaveChangesAsync();

            await SavePredictions("BothTeamsScore", _dataAnalyzerService.BothTeamsScore(scraped));
            await SavePredictions("Draw", _dataAnalyzerService.Draw(scraped));
            await SavePredictions("Over2.5Goals", _dataAnalyzerService.OverTwoGoals(scraped));
            await SavePredictions("StraightWin", _dataAnalyzerService.StraightWin(scraped));
            _logger.LogInformation("Predictions saved successfully.");

            await UpdatePredictionsWithActualResults();
            _logger.LogInformation("Predictions updated with actual results.");

            await AnalyzePatterns();
            _logger.LogInformation("Pattern analysis completed.");

            // Regression-based predictions using historical match scores
            var regressionPredictions = _regressionPredictorService.GeneratePredictions(scraped);
            await SavePredictions(regressionPredictions);
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

    private async Task UpdatePredictionsWithActualResults()
    {
        var today = DateTimeProvider.GetLocalTime().Date;
        var scores = await _dbContext.MatchScores
            .Where(s => s.MatchTime.Date == today)
            .ToListAsync();

        foreach (var score in scores)
        {
            var dateStr = score.MatchTime.ToString("yyyy-MM-dd");
            var timeStr = score.MatchTime.ToString("HH:mm");

            var predictions = await _dbContext.Predictions
                .Where(p => p.Date == dateStr &&
                            p.Time == timeStr &&
                            p.HomeTeam == score.HomeTeam &&
                            p.AwayTeam == score.AwayTeam)
                .ToListAsync();

            foreach (var prediction in predictions)
            {
                switch (prediction.PredictionCategory)
                {
                    case "BothTeamsScore":
                        prediction.ActualOutcome = score.BTTSLabel ? "BTTS" : "No BTTS";
                        break;
                    case "Draw":
                        prediction.ActualOutcome = DetermineDrawOutcome(score.Score);
                        break;
                    case "Over2.5Goals":
                        prediction.ActualOutcome = DetermineOver25Outcome(score.Score);
                        break;
                    case "StraightWin":
                        prediction.ActualOutcome = DetermineStraightWinOutcome(score.Score);
                        break;
                }
                prediction.ActualScore = score.Score;
            }
        }
        await _dbContext.SaveChangesAsync();
    }

    private string DetermineDrawOutcome(string score)
    {
        var parts = score.Split(':');
        if (parts.Length != 2) return "Unknown";
        
        if (int.TryParse(parts[0], out var home) && int.TryParse(parts[1], out var away))
        {
            return home == away ? "Draw" : "Not Draw";
        }
        return "Unknown";
    }

    private string DetermineOver25Outcome(string score)
    {
        var parts = score.Split(':');
        if (parts.Length != 2) return "Unknown";
        
        if (int.TryParse(parts[0], out var home) && int.TryParse(parts[1], out var away))
        {
            return (home + away) > 2 ? "Over 2.5" : "Under 2.5";
        }
        return "Unknown";
    }

    private string DetermineStraightWinOutcome(string score)
    {
        var parts = score.Split(':');
        if (parts.Length != 2) return "Unknown";
        
        if (int.TryParse(parts[0], out var home) && int.TryParse(parts[1], out var away))
        {
            if (home > away) return "Home Win";
            if (home < away) return "Away Win";
            return "Draw";
        }
        return "Unknown";
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
    public async Task CleanupOldPredictionsAsync()
    {
        var cutoff = DateTimeProvider.GetLocalTime().AddDays(-2);

        var oldPredictions = (await _dbContext.Predictions.ToListAsync())
            .Where(p => DateTime.Parse(p.Date) < cutoff)
            .ToList();

        if (oldPredictions.Count > 0)
        {
            _dbContext.Predictions.RemoveRange(oldPredictions);
            await _dbContext.SaveChangesAsync();
        }
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
                ConfidenceScore = category switch
                {
                    "BothTeamsScore" => 0,
                    "Draw" => (decimal?)match.Draw,
                    "Over2.5Goals" => (decimal?)match.OverTwoGoals,
                    "StraightWin" => (decimal?)(match.HomeWin > match.AwayWin ? match.HomeWin : match.AwayWin),
                    _ => null
                },
                Date = properDateTime.date,
                Time = properDateTime.time,
            };

            var exists = await _dbContext.Predictions.AnyAsync(p =>
                p.HomeTeam == prediction.HomeTeam &&
                p.AwayTeam == prediction.AwayTeam &&
                p.League == prediction.League &&
                p.Date == prediction.Date &&
                p.Time == prediction.Time &&
                p.PredictedOutcome == prediction.PredictedOutcome &&
                p.PredictionCategory == prediction.PredictionCategory);

            if (!exists)
            {
                _dbContext.Predictions.Add(prediction);
            }
        }

        await _dbContext.SaveChangesAsync();
    }

    private async Task SavePredictions(IEnumerable<Prediction> predictions)
    {
        foreach (var prediction in predictions)
        {
            var exists = await _dbContext.Predictions.AnyAsync(p =>
                p.HomeTeam == prediction.HomeTeam &&
                p.AwayTeam == prediction.AwayTeam &&
                p.League == prediction.League &&
                p.Date == prediction.Date &&
                p.Time == prediction.Time &&
                p.PredictedOutcome == prediction.PredictedOutcome &&
                p.PredictionCategory == prediction.PredictionCategory);

            if (!exists)
            {
                _dbContext.Predictions.Add(prediction);
            }
        }

        await _dbContext.SaveChangesAsync();
    }

}
