using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
            .Where(m => m.Date == matchDateString)
            .OrderBy(m => m.Date)
            .ThenBy(m => m.Time)
            .ThenBy(m => m.HomeTeam)
            .ToListAsync();
    }
}