using System.Text.Json;
using MatchPredictor.Domain.Helpers;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MatchPredictor.Infrastructure.Services;

public sealed class FixtureFeatureService : IFixtureFeatureService
{
    private const int FormWindow = 5;
    private readonly ApplicationDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FixtureFeatureService> _logger;

    public FixtureFeatureService(
        ApplicationDbContext dbContext,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<FixtureFeatureService> logger)
    {
        _dbContext = dbContext;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task CaptureFeatureSnapshotsAsync(
        IReadOnlyCollection<MatchData> fixtures,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = DateTime.UtcNow;
        var eligibleFixtures = fixtures
            .Where(fixture => !string.IsNullOrWhiteSpace(fixture.FixtureKey))
            .Where(fixture => fixture.MatchDateTime is null || fixture.MatchDateTime > nowUtc)
            .ToList();
        if (eligibleFixtures.Count == 0)
        {
            return;
        }

        var earliestKickoff = eligibleFixtures
            .Select(fixture => fixture.MatchDateTime ?? nowUtc)
            .Min();
        var historyStart = earliestKickoff.AddDays(-365);
        var history = await _dbContext.MatchScores
            .AsNoTracking()
            .Where(score => !score.IsLive && score.MatchTime >= historyStart && score.MatchTime < earliestKickoff)
            .OrderByDescending(score => score.MatchTime)
            .ToListAsync(cancellationToken);

        var existingFixtureKeys = await _dbContext.FixtureFeatureSnapshots
            .AsNoTracking()
            .Where(snapshot => snapshot.CapturedAtUtc >= nowUtc.AddHours(-6))
            .Select(snapshot => snapshot.FixtureKey)
            .Distinct()
            .ToListAsync(cancellationToken);
        var recentSnapshotKeys = existingFixtureKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var apiKey = ResolveApiFootballKey();
        var apiCache = new Dictionary<string, ApiTeamFeatureSet>(StringComparer.OrdinalIgnoreCase);
        var snapshots = new List<FixtureFeatureSnapshot>();

        foreach (var fixture in eligibleFixtures)
        {
            if (recentSnapshotKeys.Contains(fixture.FixtureKey))
            {
                continue;
            }

            var snapshot = BuildInternalSnapshot(fixture, history, nowUtc);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                await EnrichFromApiFootballAsync(snapshot, apiKey, apiCache, cancellationToken);
            }

            snapshots.Add(snapshot);
        }

        if (snapshots.Count == 0)
        {
            return;
        }

        _dbContext.FixtureFeatureSnapshots.AddRange(snapshots);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Captured {Count} fixture feature snapshot(s).", snapshots.Count);
    }

    private FixtureFeatureSnapshot BuildInternalSnapshot(
        MatchData fixture,
        IReadOnlyList<MatchScore> history,
        DateTime capturedAtUtc)
    {
        var cutoffUtc = fixture.MatchDateTime ?? capturedAtUtc;
        var homeHistory = history
            .Where(score => score.MatchTime < cutoffUtc)
            .Where(score =>
                TeamMatches(score.HomeTeam, fixture.HomeTeam, score.League, fixture.League) ||
                TeamMatches(score.AwayTeam, fixture.HomeTeam, score.League, fixture.League))
            .Take(FormWindow)
            .ToList();
        var awayHistory = history
            .Where(score => score.MatchTime < cutoffUtc)
            .Where(score =>
                TeamMatches(score.HomeTeam, fixture.AwayTeam, score.League, fixture.League) ||
                TeamMatches(score.AwayTeam, fixture.AwayTeam, score.League, fixture.League))
            .Take(FormWindow)
            .ToList();
        var h2h = history
            .Where(score => score.MatchTime < cutoffUtc)
            .Where(score =>
                (TeamMatches(score.HomeTeam, fixture.HomeTeam, score.League, fixture.League) &&
                 TeamMatches(score.AwayTeam, fixture.AwayTeam, score.League, fixture.League)) ||
                (TeamMatches(score.HomeTeam, fixture.AwayTeam, score.League, fixture.League) &&
                 TeamMatches(score.AwayTeam, fixture.HomeTeam, score.League, fixture.League)))
            .Take(FormWindow)
            .ToList();

        var homeForm = SummarizeTeamForm(homeHistory, fixture.HomeTeam, fixture.League);
        var awayForm = SummarizeTeamForm(awayHistory, fixture.AwayTeam, fixture.League);
        var h2hSummary = SummarizeHeadToHead(h2h, fixture.HomeTeam, fixture.AwayTeam, fixture.League);

        return new FixtureFeatureSnapshot
        {
            FixtureKey = fixture.FixtureKey,
            MatchLocalDate = fixture.MatchLocalDate ?? DateOnly.FromDateTime(cutoffUtc),
            MatchDateTimeUtc = fixture.MatchDateTime,
            League = fixture.League?.Trim() ?? string.Empty,
            HomeTeam = fixture.HomeTeam?.Trim() ?? string.Empty,
            AwayTeam = fixture.AwayTeam?.Trim() ?? string.Empty,
            CapturedAtUtc = capturedAtUtc,
            SourceName = "InternalHistory",
            HomeRestDays = ResolveRestDays(homeHistory, cutoffUtc),
            AwayRestDays = ResolveRestDays(awayHistory, cutoffUtc),
            HomeFormPointsPerMatch = homeForm.PointsPerMatch,
            AwayFormPointsPerMatch = awayForm.PointsPerMatch,
            HomeFormGoalsForPerMatch = homeForm.GoalsForPerMatch,
            AwayFormGoalsForPerMatch = awayForm.GoalsForPerMatch,
            HomeFormGoalsAgainstPerMatch = homeForm.GoalsAgainstPerMatch,
            AwayFormGoalsAgainstPerMatch = awayForm.GoalsAgainstPerMatch,
            HeadToHeadHomeWins = h2hSummary.HomeWins,
            HeadToHeadDraws = h2hSummary.Draws,
            HeadToHeadAwayWins = h2hSummary.AwayWins
        };
    }

    private async Task EnrichFromApiFootballAsync(
        FixtureFeatureSnapshot snapshot,
        string apiKey,
        IDictionary<string, ApiTeamFeatureSet> apiCache,
        CancellationToken cancellationToken)
    {
        try
        {
            var home = await LoadApiTeamFeatureSetAsync(snapshot.HomeTeam, apiKey, apiCache, cancellationToken);
            var away = await LoadApiTeamFeatureSetAsync(snapshot.AwayTeam, apiKey, apiCache, cancellationToken);
            if (home is null && away is null)
            {
                return;
            }

            snapshot.SourceName = "InternalHistory+ApiFootball";
            snapshot.HomeRestDays ??= home?.RestDays;
            snapshot.AwayRestDays ??= away?.RestDays;
            snapshot.HomeFormPointsPerMatch = home?.PointsPerMatch ?? snapshot.HomeFormPointsPerMatch;
            snapshot.AwayFormPointsPerMatch = away?.PointsPerMatch ?? snapshot.AwayFormPointsPerMatch;
            snapshot.HomeExpectedGoalsFor = home?.ExpectedGoalsFor;
            snapshot.AwayExpectedGoalsFor = away?.ExpectedGoalsFor;
            snapshot.RawPayloadJson = JsonSerializer.Serialize(new
            {
                home = home?.RawSummary,
                away = away?.RawSummary
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "API-Football feature enrichment failed for {HomeTeam} vs {AwayTeam}.", snapshot.HomeTeam, snapshot.AwayTeam);
        }
    }

    private async Task<ApiTeamFeatureSet?> LoadApiTeamFeatureSetAsync(
        string teamName,
        string apiKey,
        IDictionary<string, ApiTeamFeatureSet> apiCache,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(teamName))
        {
            return null;
        }

        if (apiCache.TryGetValue(teamName, out var cached))
        {
            return cached;
        }

        var teamId = await ResolveApiFootballTeamIdAsync(teamName, apiKey, cancellationToken);
        if (!teamId.HasValue)
        {
            return null;
        }

        var client = CreateApiFootballClient(apiKey);
        var baseUrl = ResolveApiFootballBaseUrl();
        var response = await client.GetAsync($"{baseUrl}/fixtures?team={teamId.Value}&last={FormWindow}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        var featureSet = ParseApiTeamFeatureSet(payload, teamId.Value);
        apiCache[teamName] = featureSet;
        return featureSet;
    }

    private async Task<int?> ResolveApiFootballTeamIdAsync(string teamName, string apiKey, CancellationToken cancellationToken)
    {
        var client = CreateApiFootballClient(apiKey);
        var response = await client.GetAsync($"{ResolveApiFootballBaseUrl()}/teams?search={Uri.EscapeDataString(teamName)}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("response", out var responseElement) ||
            responseElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var normalizedTeam = TeamNameNormalizer.NormalizeAlias(teamName);
        foreach (var entry in responseElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("team", out var teamElement))
            {
                continue;
            }

            var candidateName = ReadString(teamElement, "name");
            if (!string.Equals(TeamNameNormalizer.NormalizeAlias(candidateName), normalizedTeam, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (teamElement.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var id))
            {
                return id;
            }
        }

        return null;
    }

    private ApiTeamFeatureSet ParseApiTeamFeatureSet(string payload, int teamId)
    {
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("response", out var responseElement) ||
            responseElement.ValueKind != JsonValueKind.Array)
        {
            return new ApiTeamFeatureSet(null, null, null, "{}");
        }

        var points = 0;
        var goalsFor = 0;
        var matchCount = 0;
        DateTime? latestMatch = null;
        foreach (var entry in responseElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("teams", out var teamsElement) ||
                !entry.TryGetProperty("goals", out var goalsElement))
            {
                continue;
            }

            var isHome = ReadInt(teamsElement.GetProperty("home"), "id") == teamId;
            var teamGoals = ReadInt(goalsElement, isHome ? "home" : "away");
            var opponentGoals = ReadInt(goalsElement, isHome ? "away" : "home");
            if (!teamGoals.HasValue || !opponentGoals.HasValue)
            {
                continue;
            }

            matchCount++;
            goalsFor += teamGoals.Value;
            points += teamGoals > opponentGoals ? 3 : teamGoals == opponentGoals ? 1 : 0;

            if (entry.TryGetProperty("fixture", out var fixtureElement) &&
                DateTime.TryParse(ReadString(fixtureElement, "date"), out var parsedDate))
            {
                latestMatch = latestMatch is null || parsedDate > latestMatch ? parsedDate : latestMatch;
            }
        }

        var restDays = latestMatch.HasValue ? Math.Max((DateTime.UtcNow - latestMatch.Value.ToUniversalTime()).TotalDays, 0.0) : (double?)null;
        return new ApiTeamFeatureSet(
            matchCount > 0 ? points / (double)matchCount : null,
            matchCount > 0 ? goalsFor / (double)matchCount : null,
            restDays,
            JsonSerializer.Serialize(new { teamId, matchCount }));
    }

    private string? ResolveApiFootballKey()
    {
        var apiKey = _configuration["ApiFootball:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey) ||
            apiKey.Contains("stored in user-secrets", StringComparison.OrdinalIgnoreCase) ||
            apiKey.Contains("set via environment variable", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return apiKey;
    }

    private HttpClient CreateApiFootballClient(string apiKey)
    {
        var client = _httpClientFactory.CreateClient("ApiFootball");
        client.DefaultRequestHeaders.Remove("x-apisports-key");
        client.DefaultRequestHeaders.Add("x-apisports-key", apiKey);
        return client;
    }

    private string ResolveApiFootballBaseUrl() =>
        (_configuration["ApiFootball:BaseUrl"] ?? "https://v3.football.api-sports.io").TrimEnd('/');

    private static TeamFormSummary SummarizeTeamForm(
        IReadOnlyCollection<MatchScore> matches,
        string? team,
        string? league)
    {
        var points = 0;
        var goalsFor = 0;
        var goalsAgainst = 0;
        var count = 0;
        foreach (var match in matches)
        {
            if (!TryParseScore(match.Score, out var homeGoals, out var awayGoals))
            {
                continue;
            }

            var isHome = TeamMatches(match.HomeTeam, team, match.League, league);
            var teamGoals = isHome ? homeGoals : awayGoals;
            var opponentGoals = isHome ? awayGoals : homeGoals;
            count++;
            goalsFor += teamGoals;
            goalsAgainst += opponentGoals;
            points += teamGoals > opponentGoals ? 3 : teamGoals == opponentGoals ? 1 : 0;
        }

        return count == 0
            ? new TeamFormSummary(null, null, null)
            : new TeamFormSummary(points / (double)count, goalsFor / (double)count, goalsAgainst / (double)count);
    }

    private static HeadToHeadSummary SummarizeHeadToHead(
        IReadOnlyCollection<MatchScore> matches,
        string? homeTeam,
        string? awayTeam,
        string? league)
    {
        var homeWins = 0;
        var draws = 0;
        var awayWins = 0;
        foreach (var match in matches)
        {
            if (!TryParseScore(match.Score, out var homeGoals, out var awayGoals))
            {
                continue;
            }

            var queriedHomeWasHome = TeamMatches(match.HomeTeam, homeTeam, match.League, league);
            var queriedHomeGoals = queriedHomeWasHome ? homeGoals : awayGoals;
            var queriedAwayGoals = queriedHomeWasHome ? awayGoals : homeGoals;
            if (queriedHomeGoals > queriedAwayGoals) homeWins++;
            else if (queriedHomeGoals == queriedAwayGoals) draws++;
            else awayWins++;
        }

        return new HeadToHeadSummary(homeWins, draws, awayWins);
    }

    private static double? ResolveRestDays(IReadOnlyList<MatchScore> history, DateTime cutoffUtc) =>
        history.Count == 0 ? null : Math.Max((cutoffUtc - history[0].MatchTime).TotalDays, 0.0);

    /// <summary>
    /// Strict qualifier-aware match (rejects Arsenal vs Arsenal W, Barcelona vs Barcelona B).
    /// Uses the same gate as settlement — not the soft admin-hint variant.
    /// </summary>
    private static bool TeamMatches(string? left, string? right, string? leftLeague, string? rightLeague) =>
        TeamIdentityMatcher.GetTeamMatchResult(left, right, leftLeague, rightLeague).IsMatch;

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

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : null;

    private sealed record TeamFormSummary(double? PointsPerMatch, double? GoalsForPerMatch, double? GoalsAgainstPerMatch);
    private sealed record HeadToHeadSummary(int HomeWins, int Draws, int AwayWins);
    private sealed record ApiTeamFeatureSet(double? PointsPerMatch, double? ExpectedGoalsFor, double? RestDays, string RawSummary);
}
