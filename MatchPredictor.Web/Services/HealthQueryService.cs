using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MatchPredictor.Web.Services;

public interface IHealthQueryService
{
    Task<List<ScrapingLog>> GetRecentScrapingLogsAsync(
        IReadOnlyList<string> eventNames,
        int limit,
        CancellationToken cancellationToken = default);
}

public class HealthQueryService : IHealthQueryService
{
    private readonly ApplicationDbContext _dbContext;

    public HealthQueryService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<List<ScrapingLog>> GetRecentScrapingLogsAsync(
        IReadOnlyList<string> eventNames,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.ScrapingLogs
            .AsNoTracking()
            .Where(log => eventNames.Contains(log.EventName))
            .OrderByDescending(log => log.Timestamp)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }
}
