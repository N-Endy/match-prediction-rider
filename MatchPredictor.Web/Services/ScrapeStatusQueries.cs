using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MatchPredictor.Web.Services;

public interface IScrapeStatusQueries
{
    Task<IReadOnlyList<ScrapingLog>> GetRecentLogsAsync(int limit, CancellationToken cancellationToken = default);
}

public sealed class ScrapeStatusQueries : IScrapeStatusQueries
{
    private readonly ApplicationDbContext _dbContext;

    public ScrapeStatusQueries(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<ScrapingLog>> GetRecentLogsAsync(int limit, CancellationToken cancellationToken = default)
    {
        return await _dbContext.ScrapingLogs
            .AsNoTracking()
            .OrderByDescending(log => log.Timestamp)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }
}
