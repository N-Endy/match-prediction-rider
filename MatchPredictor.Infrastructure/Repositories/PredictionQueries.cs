using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Utils;
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

    public Task<IReadOnlyList<Prediction>> GetUnder25Async(DateTime date) =>
        GetByCategoryAsync(date, "Under2.5Goals");

    public Task<IReadOnlyList<Prediction>> GetStraightWinAsync(DateTime date) =>
        GetByCategoryAsync(date, "StraightWin");

    public Task<IReadOnlyList<Prediction>> GetDrawAsync(DateTime date) =>
        GetByCategoryAsync(date, "Draw");

    public async Task<IReadOnlyList<Prediction>> GetCombinedSampleAsync(DateTime date, int count)
    {
        var localDate = DateOnly.FromDateTime(date);

        var predictionsForDay = await _context.Predictions
            .AsNoTracking()
            .Where(p => p.MatchLocalDate == localDate && p.IsCurrentRevision)
            .ToListAsync();

        var random = new Random();

        return predictionsForDay
            .DistinctBy(p => !string.IsNullOrWhiteSpace(p.FixtureKey)
                ? p.FixtureKey
                : $"{Normalize(p.League)}|{Normalize(p.HomeTeam)}|{Normalize(p.AwayTeam)}|{p.MatchLocalDate}")
            .OrderBy(_ => random.Next())
            .Take(count)
            .OrderBy(p => p.MatchLocalTime ?? DateTimeProvider.ParseLocalTimeOrNull(p.Time))
            .ThenBy(p => p.League)
            .ThenBy(p => p.HomeTeam)
            .ToList();
    }

    private async Task<IReadOnlyList<Prediction>> GetByCategoryAsync(DateTime date, string category)
    {
        var localDate = DateOnly.FromDateTime(date);

        var filteredPredictions = await _context.Predictions
            .AsNoTracking()
            .Where(p => p.PredictionCategory == category && p.MatchLocalDate == localDate && p.IsCurrentRevision)
            .ToListAsync();

        var list = filteredPredictions
            .OrderBy(p => p.MatchLocalTime ?? DateTimeProvider.ParseLocalTimeOrNull(p.Time))
            .ThenBy(p => p.League)
            .ThenBy(p => p.HomeTeam)
            .ToList();

        return list
            .DistinctBy(p => !string.IsNullOrWhiteSpace(p.FixtureKey)
                ? p.FixtureKey
                : $"{Normalize(p.League)}|{Normalize(p.HomeTeam)}|{Normalize(p.AwayTeam)}|{p.MatchLocalDate}")
            .ToList();
    }

    private static string Normalize(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }
}
