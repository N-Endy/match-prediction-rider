using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Utils;
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
        var matchLocalDate = DateOnly.FromDateTime(date ?? DateTimeProvider.GetLocalTime());

        return await _context.MatchDatas
            .AsNoTracking()
            .Where(m => m.MatchLocalDate == matchLocalDate)
            .OrderBy(m => m.MatchDateTime ?? DateTimeProvider.ConvertLocalToUtc(
                matchLocalDate.ToDateTime(m.MatchLocalTime ?? new TimeOnly(0, 0), DateTimeKind.Unspecified)))
            .ThenBy(m => m.MatchLocalTime)
            .ThenBy(m => m.HomeTeam)
            .ToListAsync();
    }
}
