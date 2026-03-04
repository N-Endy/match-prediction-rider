using System.Globalization;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MatchPredictor.Infrastructure.Repositories;

public class MatchDataRepository : IMatchDataRepository
{
    private readonly ApplicationDbContext _context;

    public MatchDataRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<MatchData>> GetMatchDataAsync(DateTime? date = null)
    {
        var matchDate = date?.Date ?? DateTime.UtcNow.Date;
        var matchDateString = matchDate.ToString("dd-MM-yyyy");

        return await _context.MatchDatas
            .Where(m =>
                (m.MatchDateTime.HasValue && m.MatchDateTime.Value.Date == matchDate) ||
                (!m.MatchDateTime.HasValue && m.Date == matchDateString))
            .OrderBy(m => m.MatchDateTime ?? DateTime.ParseExact(m.Date ?? matchDateString, "dd-MM-yyyy", CultureInfo.InvariantCulture))
            .ThenBy(m => m.Time)
            .ThenBy(m => m.HomeTeam)
            .ToListAsync();
    }
}