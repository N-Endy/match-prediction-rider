using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MatchPredictor.Infrastructure.Repositories;

public class BetslipQueries : IBetslipQueries
{
    private readonly ApplicationDbContext _context;

    public BetslipQueries(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<BetslipSet?> GetCurrentSetAsync(CancellationToken ct = default)
    {
        return await _context.BetslipSets
            .AsNoTracking()
            .Include(s => s.Slips)
                .ThenInclude(slip => slip.Selections)
            .Where(s => s.IsCurrent)
            .OrderByDescending(s => s.GeneratedAtUtc)
            .FirstOrDefaultAsync(ct);
    }
}
