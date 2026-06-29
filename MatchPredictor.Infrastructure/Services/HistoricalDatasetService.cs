using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Domain.Sourcing;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MatchPredictor.Infrastructure.Services;

public sealed class HistoricalDatasetService : IHistoricalDatasetService
{
    private readonly ApplicationDbContext _dbContext;

    public HistoricalDatasetService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<string> ExportAsync(int lookbackDays, CancellationToken cancellationToken = default)
    {
        var window = Math.Max(1, lookbackDays);
        var cutoff = DateTime.UtcNow.AddDays(-window);

        var scores = await _dbContext.MatchScores
            .AsNoTracking()
            .Where(score => !score.IsLive)
            .Where(score => score.MatchTime >= cutoff)
            .ToListAsync(cancellationToken);

        return HistoricalDatasetSerializer.Serialize(scores, DateTime.UtcNow);
    }

    public async Task<int> ImportAsync(
        string datasetJson,
        bool replaceExisting,
        CancellationToken cancellationToken = default)
    {
        var incoming = HistoricalDatasetSerializer.Deserialize(datasetJson);
        if (incoming.Count == 0)
        {
            return 0;
        }

        if (replaceExisting)
        {
            var minTime = incoming.Min(score => score.MatchTime);
            var maxTime = incoming.Max(score => score.MatchTime);
            var existing = await _dbContext.MatchScores
                .Where(score => score.MatchTime >= minTime && score.MatchTime <= maxTime)
                .ToListAsync(cancellationToken);
            _dbContext.MatchScores.RemoveRange(existing);
        }
        else
        {
            incoming = await DeduplicateAgainstExisting(incoming, cancellationToken);
        }

        var toInsert = incoming
            .Select(score => new MatchScore
            {
                League = score.League,
                HomeTeam = score.HomeTeam,
                AwayTeam = score.AwayTeam,
                Score = score.Score,
                MatchTime = score.MatchTime,
                BTTSLabel = score.BTTSLabel,
                IsLive = score.IsLive
            })
            .ToList();

        await _dbContext.MatchScores.AddRangeAsync(toInsert, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return toInsert.Count;
    }

    private async Task<IReadOnlyList<MatchScore>> DeduplicateAgainstExisting(
        IReadOnlyList<MatchScore> incoming,
        CancellationToken cancellationToken)
    {
        var minTime = incoming.Min(score => score.MatchTime);
        var maxTime = incoming.Max(score => score.MatchTime);
        var existing = await _dbContext.MatchScores
            .AsNoTracking()
            .Where(score => score.MatchTime >= minTime && score.MatchTime <= maxTime)
            .ToListAsync(cancellationToken);

        var existingKeys = existing
            .Select(BuildKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return incoming
            .Where(score => existingKeys.Add(BuildKey(score)))
            .ToList();
    }

    private static string BuildKey(MatchScore score)
    {
        return string.Join(
            "|",
            score.MatchTime.ToUniversalTime().ToString("O"),
            score.League.Trim(),
            score.HomeTeam.Trim(),
            score.AwayTeam.Trim());
    }
}
