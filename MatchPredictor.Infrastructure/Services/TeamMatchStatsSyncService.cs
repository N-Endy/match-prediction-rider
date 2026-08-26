using MatchPredictor.Domain.Helpers;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatchPredictor.Infrastructure.Services;

public sealed class TeamMatchStatsSyncService : ITeamMatchStatsSyncService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<TeamMatchStatsSyncService> _logger;

    public TeamMatchStatsSyncService(
        ApplicationDbContext dbContext,
        ILogger<TeamMatchStatsSyncService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<int> SyncFromMatchScoresAsync(
        int lookbackDays = 600,
        CancellationToken cancellationToken = default)
    {
        var cutoffUtc = DateTime.UtcNow.AddDays(-Math.Abs(lookbackDays));
        var scores = await _dbContext.MatchScores
            .AsNoTracking()
            .Where(score => !score.IsLive && score.MatchTime >= cutoffUtc)
            .OrderBy(score => score.MatchTime)
            .ToListAsync(cancellationToken);

        if (scores.Count == 0)
        {
            return 0;
        }

        var existingKeys = await _dbContext.TeamMatchStats
            .AsNoTracking()
            .Where(row => row.SourceName == "FlashScore" && row.KickoffUtc >= cutoffUtc)
            .Select(row => new { row.SourceMatchId, row.IsHome })
            .ToListAsync(cancellationToken);
        var existing = existingKeys
            .Where(row => !string.IsNullOrWhiteSpace(row.SourceMatchId))
            .Select(row => $"{row.SourceMatchId}|{(row.IsHome ? "H" : "A")}")
            .ToHashSet(StringComparer.Ordinal);

        var pending = new List<TeamMatchStats>();
        var nowUtc = DateTime.UtcNow;

        foreach (var score in scores)
        {
            if (!TryParseScore(score.Score, out var homeGoals, out var awayGoals))
            {
                continue;
            }

            var sourceMatchId = BuildSourceMatchId(score);
            var homeKey = $"{sourceMatchId}|H";
            var awayKey = $"{sourceMatchId}|A";
            var availableFrom = score.MatchTime.AddDays(1);

            if (!existing.Contains(homeKey))
            {
                pending.Add(new TeamMatchStats
                {
                    KickoffUtc = score.MatchTime,
                    MatchLocalDate = score.MatchLocalDate != default
                        ? score.MatchLocalDate
                        : DateOnly.FromDateTime(score.MatchTime),
                    LeagueKey = string.IsNullOrWhiteSpace(score.LeagueKey)
                        ? TeamNameNormalizer.NormalizeLeagueScope(score.League)
                        : score.LeagueKey,
                    IsHome = true,
                    TeamName = score.HomeTeam,
                    OpponentName = score.AwayTeam,
                    League = score.League,
                    GoalsFor = (short)homeGoals,
                    GoalsAgainst = (short)awayGoals,
                    // SofaScore ingest has no xG today; leave null until a licensed feed is wired.
                    ExpectedGoalsFor = null,
                    ExpectedGoalsAgainst = null,
                    SourceName = "FlashScore",
                    SourceMatchId = sourceMatchId,
                    ObservedAtUtc = nowUtc,
                    AvailableFromUtc = availableFrom,
                    IsRetroactive = false
                });
                existing.Add(homeKey);
            }

            if (!existing.Contains(awayKey))
            {
                pending.Add(new TeamMatchStats
                {
                    KickoffUtc = score.MatchTime,
                    MatchLocalDate = score.MatchLocalDate != default
                        ? score.MatchLocalDate
                        : DateOnly.FromDateTime(score.MatchTime),
                    LeagueKey = string.IsNullOrWhiteSpace(score.LeagueKey)
                        ? TeamNameNormalizer.NormalizeLeagueScope(score.League)
                        : score.LeagueKey,
                    IsHome = false,
                    TeamName = score.AwayTeam,
                    OpponentName = score.HomeTeam,
                    League = score.League,
                    GoalsFor = (short)awayGoals,
                    GoalsAgainst = (short)homeGoals,
                    ExpectedGoalsFor = null,
                    ExpectedGoalsAgainst = null,
                    SourceName = "FlashScore",
                    SourceMatchId = sourceMatchId,
                    ObservedAtUtc = nowUtc,
                    AvailableFromUtc = availableFrom,
                    IsRetroactive = false
                });
                existing.Add(awayKey);
            }
        }

        if (pending.Count == 0)
        {
            return 0;
        }

        _dbContext.TeamMatchStats.AddRange(pending);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Synced {Count} TeamMatchStats row(s) from MatchScores (xG columns remain null until a licensed source is connected).",
            pending.Count);
        return pending.Count;
    }

    private static string BuildSourceMatchId(MatchScore score) =>
        $"{score.MatchLocalDate:yyyy-MM-dd}|{score.HomeTeamKey}|{score.AwayTeamKey}|{score.LeagueKey}|{score.Id}";

    private static bool TryParseScore(string? score, out int home, out int away)
    {
        home = 0;
        away = 0;
        if (string.IsNullOrWhiteSpace(score))
        {
            return false;
        }

        var normalized = score.Replace("–", "-").Replace("—", "-").Trim();
        var parts = normalized.Contains(':')
            ? normalized.Split(':', StringSplitOptions.TrimEntries)
            : normalized.Split('-', StringSplitOptions.TrimEntries);

        return parts.Length == 2 &&
               int.TryParse(parts[0], out home) &&
               int.TryParse(parts[1], out away);
    }
}
