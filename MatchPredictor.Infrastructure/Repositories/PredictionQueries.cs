using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Extensions;
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

    public Task<IReadOnlyList<Prediction>> GetDrawAsync(DateTime date) =>
        GetByCategoryAsync(date, "Draw");

    public Task<IReadOnlyList<Prediction>> GetStraightWinAsync(DateTime date) =>
        GetByCategoryAsync(date, "StraightWin");

    public async Task<IReadOnlyList<Prediction>> GetCombinedSampleAsync(DateTime date, int count)
    {
        // 1. Force the date to UTC and set up the start/end bounds
        var today = DateTimeProvider.GetLocalTime().Date;
        var (startOfDayUtc, endOfDayUtc) = today.GetUtcDayBounds();
        // var startOfDayUtc = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        // var endOfDayUtc = startOfDayUtc.AddDays(1);
    
        // 2. Evaluate the string outside the LINQ query for better EF Core translation
        var dateString = date.ToString("dd-MM-yyyy");

        // 3. Fetch data using the index-friendly UTC range
        var predictionsForDay = await _context.Predictions
            .Where(p =>
                (p.MatchDateTime >= startOfDayUtc && p.MatchDateTime < endOfDayUtc) ||
                (!p.MatchDateTime.HasValue && p.Date == dateString))
            .ToListAsync(); // Pulls the day's records into memory

        var random = new Random();

        // 4. Process the in-memory list safely
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
        // 1. Force the date to UTC to prevent Npgsql exceptions
        var today = DateTimeProvider.GetLocalTime().Date;
        var (startOfDayUtc, endOfDayUtc) = today.GetUtcDayBounds();
        // var startOfDayUtc = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        // var endOfDayUtc = startOfDayUtc.AddDays(1);
    
        // String matching remains the same
        var dateString = date.ToString("dd-MM-yyyy");

        // 2. Use a range query (>= and <) so PostgreSQL can use indexes
        var filteredPredictions = await _context.Predictions
            .Where(p =>
                p.PredictionCategory == category &&
                (
                    (p.MatchDateTime >= startOfDayUtc && p.MatchDateTime < endOfDayUtc) ||
                    (!p.MatchDateTime.HasValue && p.Date == dateString)
                ))
            .ToListAsync();

        // 3. Sort the data in-memory 
        var list = filteredPredictions
            .OrderBy(p => p.MatchDateTime ?? DateTime.Parse(p.Date)) 
            .ThenBy(p => p.Time)
            .ThenBy(p => p.League)
            .ThenBy(p => p.HomeTeam)
            .ToList();

        return list
            .DistinctBy(p => new { p.League, p.HomeTeam, p.AwayTeam, p.Date, p.Time })
            .ToList();
    }
}

