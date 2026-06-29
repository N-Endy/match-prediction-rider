using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MatchPredictor.Web.Services;

public sealed record PredictionCoverageCounts(int Total, int Live);

public sealed record SourceQualityProfilesSnapshot(
    IReadOnlyList<SourceQualityProfile> Overall,
    IReadOnlyList<SourceQualityProfile> Weakest);

public interface IHealthQueryService
{
    Task<List<ScrapingLog>> GetRecentScrapingLogsAsync(
        IReadOnlyList<string> eventNames,
        int limit,
        CancellationToken cancellationToken = default);

    Task<PredictionCoverageCounts> GetPredictionCountsAsync(
        DateOnly localDate,
        CancellationToken cancellationToken = default);

    Task<SourceQualityProfilesSnapshot> GetSourceQualityProfilesAsync(
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

    public async Task<PredictionCoverageCounts> GetPredictionCountsAsync(
        DateOnly localDate,
        CancellationToken cancellationToken = default)
    {
        var total = await _dbContext.Predictions
            .AsNoTracking()
            .Where(prediction => prediction.MatchLocalDate == localDate && prediction.IsCurrentRevision)
            .CountAsync(cancellationToken);

        var live = await _dbContext.Predictions
            .AsNoTracking()
            .Where(prediction => prediction.MatchLocalDate == localDate && prediction.IsCurrentRevision && prediction.IsLive)
            .CountAsync(cancellationToken);

        return new PredictionCoverageCounts(total, live);
    }

    public async Task<SourceQualityProfilesSnapshot> GetSourceQualityProfilesAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var overall = await _dbContext.SourceQualityProfiles
                .AsNoTracking()
                .Where(profile => profile.LeagueKey == "all" && profile.TimeBucketKey == "all")
                .OrderByDescending(profile => profile.SourceName)
                .ToListAsync(cancellationToken);

            var weakest = await _dbContext.SourceQualityProfiles
                .AsNoTracking()
                .Where(profile =>
                    profile.LeagueKey != "all" &&
                    profile.TimeBucketKey != "all" &&
                    profile.SampleCount >= 4)
                .OrderBy(profile => profile.ReliabilityScore)
                .ThenByDescending(profile => profile.SampleCount)
                .Take(6)
                .ToListAsync(cancellationToken);

            return new SourceQualityProfilesSnapshot(overall, weakest);
        }
        catch (PostgresException ex) when (IsMissingSourceQualityTable(ex))
        {
            return new SourceQualityProfilesSnapshot([], []);
        }
    }

    private static bool IsMissingSourceQualityTable(PostgresException ex)
    {
        return ex.SqlState == PostgresErrorCodes.UndefinedTable &&
               string.Equals(ex.TableName, "SourceQualityProfiles", StringComparison.Ordinal);
    }
}
