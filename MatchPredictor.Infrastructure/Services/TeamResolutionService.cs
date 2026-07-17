using MatchPredictor.Domain.Helpers;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MatchPredictor.Infrastructure.Services;

public sealed class TeamResolutionService : ITeamResolutionService
{
    private readonly ApplicationDbContext _dbContext;

    public TeamResolutionService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyDictionary<string, int>> LoadAliasLookupAsync(CancellationToken cancellationToken = default)
    {
        var aliases = await _dbContext.TeamAliases
            .AsNoTracking()
            .Where(alias => alias.NormalizedAlias != string.Empty)
            .ToListAsync(cancellationToken);

        var lookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in aliases)
        {
            var scopedKey = TeamNameNormalizer.BuildAliasKey(alias.NormalizedAlias, alias.LeagueScope);
            if (!string.IsNullOrWhiteSpace(scopedKey))
            {
                lookup.TryAdd(scopedKey, alias.TeamId);
            }

            lookup.TryAdd(alias.NormalizedAlias, alias.TeamId);
        }

        return lookup;
    }

    public async Task SeedAliasesFromExistingDataAsync(CancellationToken cancellationToken = default)
    {
        var existingAliases = await _dbContext.TeamAliases
            .Select(alias => new { alias.NormalizedAlias, alias.LeagueScope, alias.SourceName })
            .ToListAsync(cancellationToken);
        var existingAliasKeys = existingAliases
            .Select(alias => BuildDuplicateKey(alias.NormalizedAlias, alias.LeagueScope, alias.SourceName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existingTeams = await _dbContext.Teams.ToListAsync(cancellationToken);
        var teamsByScopedName = existingTeams.ToDictionary(
            team => BuildDuplicateKey(team.NormalizedName, team.LeagueScope, string.Empty),
            team => team,
            StringComparer.OrdinalIgnoreCase);

        var candidates = await LoadAliasCandidatesAsync(cancellationToken);
        foreach (var candidate in candidates)
        {
            if (!TeamNameNormalizer.IsUsableAliasCandidate(candidate.TeamName, candidate.League))
            {
                continue;
            }

            var normalizedAlias = TeamNameNormalizer.NormalizeAlias(candidate.TeamName);
            if (string.IsNullOrWhiteSpace(normalizedAlias))
            {
                continue;
            }

            var leagueScope = TeamNameNormalizer.NormalizeLeagueScope(candidate.League);
            if (string.IsNullOrWhiteSpace(leagueScope))
            {
                leagueScope = null;
            }

            var teamKey = BuildDuplicateKey(normalizedAlias, leagueScope, string.Empty);
            if (!teamsByScopedName.TryGetValue(teamKey, out var team))
            {
                team = new Team
                {
                    Name = BoundDisplayName(candidate.TeamName),
                    NormalizedName = normalizedAlias,
                    LeagueScope = leagueScope,
                    CreatedAtUtc = DateTime.UtcNow
                };
                _dbContext.Teams.Add(team);
                teamsByScopedName[teamKey] = team;
            }

            var aliasKey = BuildDuplicateKey(normalizedAlias, leagueScope, candidate.SourceName);
            if (existingAliasKeys.Contains(aliasKey))
            {
                continue;
            }

            _dbContext.TeamAliases.Add(new TeamAlias
            {
                Team = team,
                Alias = BoundDisplayName(candidate.TeamName),
                NormalizedAlias = normalizedAlias,
                LeagueScope = leagueScope,
                SourceName = BoundSourceName(candidate.SourceName),
                CreatedAtUtc = DateTime.UtcNow
            });
            existingAliasKeys.Add(aliasKey);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<AliasCandidate>> LoadAliasCandidatesAsync(CancellationToken cancellationToken)
    {
        var matchDataTeams = await _dbContext.MatchDatas
            .AsNoTracking()
            .Select(match => new { match.League, match.HomeTeam, match.AwayTeam })
            .ToListAsync(cancellationToken);
        var matchScoreTeams = await _dbContext.MatchScores
            .AsNoTracking()
            .Select(score => new { League = (string?)score.League, HomeTeam = (string?)score.HomeTeam, AwayTeam = (string?)score.AwayTeam })
            .ToListAsync(cancellationToken);
        var aiScoreTeams = await _dbContext.AiScoreMatchScores
            .AsNoTracking()
            .Select(score => new { League = (string?)score.League, HomeTeam = (string?)score.HomeTeam, AwayTeam = (string?)score.AwayTeam })
            .ToListAsync(cancellationToken);
        var sofaScoreTeams = await _dbContext.SofaScoreMatchScores
            .AsNoTracking()
            .Select(score => new { League = (string?)score.League, HomeTeam = (string?)score.HomeTeam, AwayTeam = (string?)score.AwayTeam })
            .ToListAsync(cancellationToken);
        var sourceMarketTeams = await _dbContext.MarketOddsSnapshots
            .AsNoTracking()
            .Select(snapshot => new { League = (string?)snapshot.League, HomeTeam = (string?)snapshot.HomeTeam, AwayTeam = (string?)snapshot.AwayTeam })
            .ToListAsync(cancellationToken);

        return matchDataTeams.SelectMany(match => BuildCandidates(match.League, match.HomeTeam, match.AwayTeam, "sports-ai.dev"))
            .Concat(matchScoreTeams.SelectMany(match => BuildCandidates(match.League, match.HomeTeam, match.AwayTeam, "ScoreFeed")))
            .Concat(aiScoreTeams.SelectMany(match => BuildCandidates(match.League, match.HomeTeam, match.AwayTeam, "AiScore")))
            .Concat(sofaScoreTeams.SelectMany(match => BuildCandidates(match.League, match.HomeTeam, match.AwayTeam, "SofaScore")))
            .Concat(sourceMarketTeams.SelectMany(match => BuildCandidates(match.League, match.HomeTeam, match.AwayTeam, "SportyBet")))
            .Where(candidate => TeamNameNormalizer.IsUsableAliasCandidate(candidate.TeamName, candidate.League))
            .DistinctBy(candidate => BuildDuplicateKey(
                TeamNameNormalizer.NormalizeAlias(candidate.TeamName),
                TeamNameNormalizer.NormalizeLeagueScope(candidate.League),
                candidate.SourceName))
            .ToList();
    }

    private static IEnumerable<AliasCandidate> BuildCandidates(string? league, string? homeTeam, string? awayTeam, string sourceName)
    {
        if (!string.IsNullOrWhiteSpace(homeTeam))
        {
            yield return new AliasCandidate(homeTeam, league, sourceName);
        }

        if (!string.IsNullOrWhiteSpace(awayTeam))
        {
            yield return new AliasCandidate(awayTeam, league, sourceName);
        }
    }

    private static string BoundDisplayName(string teamName) =>
        TeamNameNormalizer.BoundIndexedValue(teamName.Trim());

    private static string BoundSourceName(string sourceName) =>
        TeamNameNormalizer.BoundIndexedValue(sourceName.Trim());

    private static string BuildDuplicateKey(string normalizedAlias, string? leagueScope, string sourceName) =>
        $"{leagueScope ?? string.Empty}|{normalizedAlias}|{sourceName}";

    private sealed record AliasCandidate(string TeamName, string? League, string SourceName);
}
