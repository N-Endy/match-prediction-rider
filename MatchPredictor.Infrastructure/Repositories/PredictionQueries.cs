using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MatchPredictor.Infrastructure.Repositories;

public class PredictionQueries : IPredictionQueries
{
    private readonly ApplicationDbContext _context;

    public PredictionQueries(ApplicationDbContext context)
    {
        _context = context;
    }

    public Task<IReadOnlyList<Prediction>> GetBTTSAsync(DateTime date) =>
        GetByCategoryAsync(date, "BothTeamsScore");

    public Task<IReadOnlyList<Prediction>> GetOver25Async(DateTime date) =>
        GetByCategoryAsync(date, "Over2.5Goals");

    public Task<IReadOnlyList<Prediction>> GetDrawAsync(DateTime date) =>
        GetByCategoryAsync(date, "Draw");

    public Task<IReadOnlyList<Prediction>> GetStraightWinAsync(DateTime date) =>
        GetByCategoryAsync(date, "StraightWin");

    public async Task<IReadOnlyList<Prediction>> GetCombinedSampleAsync(DateTime date, int count)
    {
        var dateString = date.ToString("dd-MM-yyyy");

        var predictionsForDay = await _context.Predictions
            .Where(p => p.Date == dateString)
            .ToListAsync();

        await SynchronizePredictionTimesAsync(predictionsForDay);

        var random = new Random();

        return predictionsForDay
            .DistinctBy(p => new { p.League, p.HomeTeam, p.AwayTeam, p.Date, p.Time })
            .OrderBy(_ => random.Next())
            .Take(count)
            .OrderBy(p => p.Time)
            .ThenBy(p => p.League)
            .ThenBy(p => p.HomeTeam)
            .ToList();
    }

    private async Task<IReadOnlyList<Prediction>> GetByCategoryAsync(DateTime date, string category)
    {
        var dateString = date.ToString("dd-MM-yyyy");

        var filteredPredictions = await _context.Predictions
            .Where(p => p.PredictionCategory == category && p.Date == dateString)
            .ToListAsync();

        await SynchronizePredictionTimesAsync(filteredPredictions);

        var list = filteredPredictions
            .OrderBy(p => p.Time)
            .ThenBy(p => p.League)
            .ThenBy(p => p.HomeTeam)
            .ToList();

        return list
            .DistinctBy(p => new { p.League, p.HomeTeam, p.AwayTeam, p.Date, p.Time })
            .ToList();
    }

    private async Task SynchronizePredictionTimesAsync(List<Prediction> predictions)
    {
        if (predictions.Count == 0)
        {
            return;
        }

        var dates = predictions
            .Select(prediction => prediction.Date)
            .Distinct()
            .ToList();

        var matches = await _context.MatchDatas
            .Where(match => match.Date != null && dates.Contains(match.Date))
            .ToListAsync();

        var matchLookup = matches
            .GroupBy(match => (
                Date: match.Date ?? string.Empty,
                Home: Normalize(match.HomeTeam),
                Away: Normalize(match.AwayTeam),
                League: Normalize(match.League)))
            .ToDictionary(group => group.Key, group => group.First());

        foreach (var prediction in predictions)
        {
            var key = (
                Date: prediction.Date,
                Home: Normalize(prediction.HomeTeam),
                Away: Normalize(prediction.AwayTeam),
                League: Normalize(prediction.League));

            if (!matchLookup.TryGetValue(key, out var match))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(match.Time))
            {
                prediction.Time = match.Time!;
            }

            if (match.MatchDateTime.HasValue)
            {
                prediction.MatchDateTime = match.MatchDateTime;
            }

            if (!string.IsNullOrWhiteSpace(match.Date))
            {
                prediction.Date = match.Date!;
            }
        }
    }

    private static string Normalize(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }
}
