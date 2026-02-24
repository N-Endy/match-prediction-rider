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
        var dateOnly = date.Date;

        var list = await _context.Predictions
            .Where(p =>
                (p.MatchDateTime.HasValue && p.MatchDateTime.Value.Date == dateOnly) ||
                (!p.MatchDateTime.HasValue && p.Date == dateOnly.ToString("dd-MM-yyyy")))
            .ToListAsync();

        var random = new Random();

        return list
            .OrderBy(_ => random.Next())
            .Take(count)
            .DistinctBy(p => new { p.League, p.HomeTeam, p.AwayTeam, p.Date, p.Time })
            .OrderBy(p => p.Time)
            .ThenBy(p => p.League)
            .ThenBy(p => p.HomeTeam)
            .ToList();
    }

    private async Task<IReadOnlyList<Prediction>> GetByCategoryAsync(DateTime date, string category)
    {
        var dateOnly = date.Date;
        var dateString = dateOnly.ToString("dd-MM-yyyy");

        var list = await _context.Predictions
            .Where(p =>
                p.PredictionCategory == category &&
                (
                    (p.MatchDateTime.HasValue && p.MatchDateTime.Value.Date == dateOnly) ||
                    (!p.MatchDateTime.HasValue && p.Date == dateString)
                ))
            .OrderBy(p => p.MatchDateTime ?? DateTime.Parse(p.Date))
            .ThenBy(p => p.Time)
            .ThenBy(p => p.League)
            .ThenBy(p => p.HomeTeam)
            .ToListAsync();

        return list
            .DistinctBy(p => new { p.League, p.HomeTeam, p.AwayTeam, p.Date, p.Time })
            .ToList();
    }
}

