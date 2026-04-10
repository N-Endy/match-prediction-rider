using System.Globalization;
using System.Text.Json;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MatchPredictor.Infrastructure.Services;

public class AiChatFootballInsightService : IAiChatFootballInsightService
{
    private const int InternalLookbackDays = 240;
    private const int OverallSampleThreshold = 3;
    private const int VenueSampleThreshold = 2;
    private const int ResultWindow = 5;
    private static readonly HashSet<string> FinishedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "FT", "AET", "PEN"
    };

    private readonly ApplicationDbContext _dbContext;
    private readonly IDistributedCache _cache;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AiChatFootballInsightService> _logger;

    public AiChatFootballInsightService(
        ApplicationDbContext dbContext,
        IDistributedCache cache,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<AiChatFootballInsightService> logger)
    {
        _dbContext = dbContext;
        _cache = cache;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, FootballMatchInsightSnapshot>> GetInsightsAsync(
        IReadOnlyCollection<AiChatFootballInsightRequest> requests,
        CancellationToken ct = default)
    {
        if (requests.Count == 0)
        {
            return new Dictionary<string, FootballMatchInsightSnapshot>();
        }

        var results = new Dictionary<string, FootballMatchInsightSnapshot>(StringComparer.OrdinalIgnoreCase);
        var missingRequests = new List<AiChatFootballInsightRequest>();

        foreach (var request in requests.Where(IsEligibleFootballRequest))
        {
            var cached = await TryGetCachedSnapshotAsync(request, ct);
            if (cached is not null)
            {
                results[request.ActionKey] = cached;
            }
            else
            {
                missingRequests.Add(request);
            }
        }

        if (missingRequests.Count == 0)
        {
            return results;
        }

        var earliestCutoffUtc = missingRequests
            .Select(ResolveCutoffUtc)
            .Min();
        var latestCutoffUtc = missingRequests
            .Select(ResolveCutoffUtc)
            .Max();
        var internalResults = await LoadInternalResultsAsync(
            earliestCutoffUtc.AddDays(-InternalLookbackDays),
            latestCutoffUtc,
            ct);
        var externalCache = new Dictionary<string, List<HistoricalFixtureResult>>(StringComparer.OrdinalIgnoreCase);

        foreach (var request in missingRequests)
        {
            var snapshot = await BuildSnapshotAsync(request, internalResults, externalCache, ct);
            results[request.ActionKey] = snapshot;
            await CacheSnapshotAsync(request, snapshot, ct);
        }

        return results;
    }

    private async Task<FootballMatchInsightSnapshot> BuildSnapshotAsync(
        AiChatFootballInsightRequest request,
        IReadOnlyList<HistoricalFixtureResult> internalResults,
        IDictionary<string, List<HistoricalFixtureResult>> externalCache,
        CancellationToken ct)
    {
        var cutoffUtc = ResolveCutoffUtc(request);

        var homeResults = FilterTeamResults(internalResults, request.HomeTeam, cutoffUtc);
        var awayResults = FilterTeamResults(internalResults, request.AwayTeam, cutoffUtc);

        var homeVenueResults = homeResults
            .Where(result => TeamPlayedAtVenue(result, request.HomeTeam, isHome: true))
            .Take(ResultWindow)
            .ToList();
        var awayVenueResults = awayResults
            .Where(result => TeamPlayedAtVenue(result, request.AwayTeam, isHome: false))
            .Take(ResultWindow)
            .ToList();

        var usedExternal = false;

        if (NeedsExternalBackfill(homeResults, homeVenueResults))
        {
            var externalResults = await GetExternalResultsAsync(request.HomeTeam, cutoffUtc, externalCache, ct);
            if (externalResults.Count > 0)
            {
                homeResults = MergeAndTakeRecent(homeResults, externalResults, request.HomeTeam);
                homeVenueResults = homeResults
                    .Where(result => TeamPlayedAtVenue(result, request.HomeTeam, isHome: true))
                    .Take(ResultWindow)
                    .ToList();
                usedExternal = true;
            }
        }

        if (NeedsExternalBackfill(awayResults, awayVenueResults))
        {
            var externalResults = await GetExternalResultsAsync(request.AwayTeam, cutoffUtc, externalCache, ct);
            if (externalResults.Count > 0)
            {
                awayResults = MergeAndTakeRecent(awayResults, externalResults, request.AwayTeam);
                awayVenueResults = awayResults
                    .Where(result => TeamPlayedAtVenue(result, request.AwayTeam, isHome: false))
                    .Take(ResultWindow)
                    .ToList();
                usedExternal = true;
            }
        }

        var homeForm = BuildTeamSnapshot(request.HomeTeam, homeResults, homeVenueResults, isHomeTeam: true);
        var awayForm = BuildTeamSnapshot(request.AwayTeam, awayResults, awayVenueResults, isHomeTeam: false);
        var headToHead = BuildHeadToHeadSummary(
            internalResults
                .Where(result => result.MatchTimeUtc < cutoffUtc)
                .Where(result => TeamsMatchFixture(result, request.HomeTeam, request.AwayTeam))
                .Take(ResultWindow)
                .ToList(),
            request.HomeTeam,
            request.AwayTeam);

        var hasInternalSample = homeForm.SampleSize > 0 || awayForm.SampleSize > 0;
        var source = usedExternal
            ? "InternalHistory+ApiFootballFallback"
            : hasInternalSample
                ? "InternalHistory"
                : "Unavailable";
        var quality = ResolveDataQuality(homeForm, awayForm);

        return new FootballMatchInsightSnapshot
        {
            HomeTeam = request.HomeTeam,
            AwayTeam = request.AwayTeam,
            HomeForm = homeForm,
            AwayForm = awayForm,
            HeadToHead = headToHead,
            InsightSource = source,
            DataQuality = quality,
            IsLowConfidence = string.Equals(quality, "Low", StringComparison.OrdinalIgnoreCase)
        };
    }

    private async Task<FootballMatchInsightSnapshot?> TryGetCachedSnapshotAsync(
        AiChatFootballInsightRequest request,
        CancellationToken ct)
    {
        var payload = await _cache.GetStringAsync(BuildCacheKey(request), ct);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<FootballMatchInsightSnapshot>(payload, JsonOptions());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to deserialize cached football insight for {ActionKey}.", request.ActionKey);
            return null;
        }
    }

    private async Task CacheSnapshotAsync(
        AiChatFootballInsightRequest request,
        FootballMatchInsightSnapshot snapshot,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(snapshot, JsonOptions());
        await _cache.SetStringAsync(
            BuildCacheKey(request),
            payload,
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(2)
            },
            ct);
    }

    private async Task<List<HistoricalFixtureResult>> LoadInternalResultsAsync(
        DateTime windowStartUtc,
        DateTime windowEndUtc,
        CancellationToken ct)
    {
        var flashResults = await _dbContext.MatchScores
            .AsNoTracking()
            .Where(score => !score.IsLive && score.MatchTime >= windowStartUtc && score.MatchTime < windowEndUtc)
            .ToListAsync(ct);
        var aiResults = await _dbContext.AiScoreMatchScores
            .AsNoTracking()
            .Where(score => !score.IsLive && score.MatchTime >= windowStartUtc && score.MatchTime < windowEndUtc)
            .ToListAsync(ct);
        var sofaResults = await _dbContext.SofaScoreMatchScores
            .AsNoTracking()
            .Where(score => !score.IsLive && score.MatchTime >= windowStartUtc && score.MatchTime < windowEndUtc)
            .ToListAsync(ct);

        var combined = new List<HistoricalFixtureResult>();
        combined.AddRange(ConvertResults(flashResults, "MatchScores", 1));
        combined.AddRange(ConvertResults(aiResults, "AiScore", 2));
        combined.AddRange(ConvertResults(sofaResults, "SofaScore", 0));

        return Deduplicate(combined);
    }

    private static IEnumerable<HistoricalFixtureResult> ConvertResults(
        IEnumerable<MatchScore> results,
        string sourceName,
        int sourcePriority)
    {
        foreach (var result in results)
        {
            if (!TryParseScore(result.Score, out var homeGoals, out var awayGoals))
            {
                continue;
            }

            yield return new HistoricalFixtureResult(
                sourceName,
                sourcePriority,
                result.League,
                result.HomeTeam,
                result.AwayTeam,
                result.MatchTime,
                homeGoals,
                awayGoals);
        }
    }

    private static IEnumerable<HistoricalFixtureResult> ConvertResults(
        IEnumerable<AiScoreMatchScore> results,
        string sourceName,
        int sourcePriority)
    {
        foreach (var result in results)
        {
            if (!TryParseScore(result.Score, out var homeGoals, out var awayGoals))
            {
                continue;
            }

            yield return new HistoricalFixtureResult(
                sourceName,
                sourcePriority,
                result.League,
                result.HomeTeam,
                result.AwayTeam,
                result.MatchTime,
                homeGoals,
                awayGoals);
        }
    }

    private static IEnumerable<HistoricalFixtureResult> ConvertResults(
        IEnumerable<SofaScoreMatchScore> results,
        string sourceName,
        int sourcePriority)
    {
        foreach (var result in results)
        {
            if (!TryParseScore(result.Score, out var homeGoals, out var awayGoals))
            {
                continue;
            }

            yield return new HistoricalFixtureResult(
                sourceName,
                sourcePriority,
                result.League,
                result.HomeTeam,
                result.AwayTeam,
                result.MatchTime,
                homeGoals,
                awayGoals);
        }
    }

    private static List<HistoricalFixtureResult> Deduplicate(IEnumerable<HistoricalFixtureResult> results)
    {
        return results
            .GroupBy(result => result.DeduplicationKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(result => result.SourcePriority)
                .ThenByDescending(result => result.MatchTimeUtc)
                .First())
            .OrderByDescending(result => result.MatchTimeUtc)
            .ToList();
    }

    private static List<HistoricalFixtureResult> FilterTeamResults(
        IEnumerable<HistoricalFixtureResult> results,
        string teamName,
        DateTime cutoffUtc)
    {
        return results
            .Where(result =>
                result.MatchTimeUtc < cutoffUtc &&
                (TeamMatches(result.HomeTeam, teamName) || TeamMatches(result.AwayTeam, teamName)))
            .OrderByDescending(result => result.MatchTimeUtc)
            .Take(ResultWindow)
            .ToList();
    }

    private static bool NeedsExternalBackfill(
        IReadOnlyCollection<HistoricalFixtureResult> overallResults,
        IReadOnlyCollection<HistoricalFixtureResult> venueResults)
    {
        return overallResults.Count < OverallSampleThreshold || venueResults.Count < VenueSampleThreshold;
    }

    private async Task<List<HistoricalFixtureResult>> GetExternalResultsAsync(
        string teamName,
        DateTime cutoffUtc,
        IDictionary<string, List<HistoricalFixtureResult>> externalCache,
        CancellationToken ct)
    {
        if (externalCache.TryGetValue(teamName, out var cached))
        {
            return cached;
        }

        var apiKey = _configuration["ApiFootball:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey) ||
            apiKey.Contains("stored in user-secrets", StringComparison.OrdinalIgnoreCase) ||
            apiKey.Contains("set via environment variable", StringComparison.OrdinalIgnoreCase))
        {
            return externalCache[teamName] = [];
        }

        try
        {
            var teamId = await ResolveApiFootballTeamIdAsync(teamName, apiKey, ct);
            if (!teamId.HasValue)
            {
                return externalCache[teamName] = [];
            }

            var client = _httpClientFactory.CreateClient("ApiFootball");
            client.DefaultRequestHeaders.Remove("x-apisports-key");
            client.DefaultRequestHeaders.Add("x-apisports-key", apiKey);

            var baseUrl = (_configuration["ApiFootball:BaseUrl"] ?? "https://v3.football.api-sports.io").TrimEnd('/');
            var response = await client.GetAsync($"{baseUrl}/fixtures?team={teamId.Value}&last=5", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("API-Football fixtures backfill returned {StatusCode} for {TeamName}.", response.StatusCode, teamName);
                return externalCache[teamName] = [];
            }

            var payload = await response.Content.ReadAsStringAsync(ct);
            var parsed = ParseApiFootballFixtures(payload, cutoffUtc);
            return externalCache[teamName] = parsed;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "API-Football backfill failed for {TeamName}.", teamName);
            return externalCache[teamName] = [];
        }
    }

    private async Task<int?> ResolveApiFootballTeamIdAsync(
        string teamName,
        string apiKey,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("ApiFootball");
        client.DefaultRequestHeaders.Remove("x-apisports-key");
        client.DefaultRequestHeaders.Add("x-apisports-key", apiKey);

        var baseUrl = (_configuration["ApiFootball:BaseUrl"] ?? "https://v3.football.api-sports.io").TrimEnd('/');
        var response = await client.GetAsync($"{baseUrl}/teams?search={Uri.EscapeDataString(teamName)}", ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var payload = await response.Content.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("response", out var responseElement) ||
            responseElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var normalizedTarget = NormalizeTeamName(teamName);
        foreach (var entry in responseElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("team", out var teamElement))
            {
                continue;
            }

            var candidateName = ReadString(teamElement, "name");
            if (string.IsNullOrWhiteSpace(candidateName))
            {
                continue;
            }

            if (NormalizeTeamName(candidateName) != normalizedTarget &&
                !NormalizeTeamName(candidateName).Contains(normalizedTarget, StringComparison.Ordinal) &&
                !normalizedTarget.Contains(NormalizeTeamName(candidateName), StringComparison.Ordinal))
            {
                continue;
            }

            var teamId = ReadInt(teamElement, "id");
            if (teamId.HasValue)
            {
                return teamId.Value;
            }
        }

        return null;
    }

    private static List<HistoricalFixtureResult> ParseApiFootballFixtures(string payload, DateTime cutoffUtc)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return [];
        }

        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("response", out var responseElement) ||
            responseElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<HistoricalFixtureResult>();
        foreach (var fixture in responseElement.EnumerateArray())
        {
            var status = ReadString(fixture, "fixture", "status", "short");
            if (string.IsNullOrWhiteSpace(status) || !FinishedStatuses.Contains(status))
            {
                continue;
            }

            var homeTeam = ReadString(fixture, "teams", "home", "name");
            var awayTeam = ReadString(fixture, "teams", "away", "name");
            var league = ReadString(fixture, "league", "name");
            var homeGoals = ReadInt(fixture, "goals", "home");
            var awayGoals = ReadInt(fixture, "goals", "away");
            var kickoffRaw = ReadString(fixture, "fixture", "date");

            if (string.IsNullOrWhiteSpace(homeTeam) ||
                string.IsNullOrWhiteSpace(awayTeam) ||
                !homeGoals.HasValue ||
                !awayGoals.HasValue ||
                !DateTime.TryParse(kickoffRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var kickoffUtc))
            {
                continue;
            }

            kickoffUtc = kickoffUtc.ToUniversalTime();
            if (kickoffUtc >= cutoffUtc)
            {
                continue;
            }

            results.Add(new HistoricalFixtureResult(
                "ApiFootball",
                3,
                league ?? string.Empty,
                homeTeam,
                awayTeam,
                kickoffUtc,
                homeGoals.Value,
                awayGoals.Value));
        }

        return Deduplicate(results);
    }

    private static List<HistoricalFixtureResult> MergeAndTakeRecent(
        IReadOnlyCollection<HistoricalFixtureResult> internalResults,
        IReadOnlyCollection<HistoricalFixtureResult> externalResults,
        string teamName)
    {
        return Deduplicate(internalResults.Concat(externalResults))
            .Where(result => TeamMatches(result.HomeTeam, teamName) || TeamMatches(result.AwayTeam, teamName))
            .OrderByDescending(result => result.MatchTimeUtc)
            .Take(ResultWindow)
            .ToList();
    }

    private static TeamFormSnapshot BuildTeamSnapshot(
        string teamName,
        IReadOnlyList<HistoricalFixtureResult> overallResults,
        IReadOnlyList<HistoricalFixtureResult> venueResults,
        bool isHomeTeam)
    {
        var snapshot = new TeamFormSnapshot
        {
            TeamName = teamName,
            SampleSize = overallResults.Count,
            VenueSampleSize = venueResults.Count,
            LastFiveOverallResults = overallResults
                .Select(result => ToMatchSummary(result, teamName))
                .ToList(),
            LastFiveVenueResults = venueResults
                .Select(result => ToMatchSummary(result, teamName))
                .ToList()
        };

        PopulateAggregateMetrics(snapshot, overallResults, teamName, venueOnly: false);
        PopulateAggregateMetrics(snapshot, venueResults, teamName, venueOnly: true);
        return snapshot;
    }

    private static void PopulateAggregateMetrics(
        TeamFormSnapshot snapshot,
        IReadOnlyList<HistoricalFixtureResult> results,
        string teamName,
        bool venueOnly)
    {
        if (results.Count == 0)
        {
            return;
        }

        var wins = 0;
        var draws = 0;
        var losses = 0;
        var goalsFor = 0d;
        var goalsAgainst = 0d;
        var bttsHits = 0;
        var over25Hits = 0;
        var under25Hits = 0;
        var cleanSheets = 0;
        var points = 0d;

        foreach (var result in results)
        {
            var (teamGoals, opponentGoals) = ResolveScorePerspective(result, teamName);
            goalsFor += teamGoals;
            goalsAgainst += opponentGoals;
            if (teamGoals > opponentGoals)
            {
                wins++;
                points += 3;
            }
            else if (teamGoals == opponentGoals)
            {
                draws++;
                points += 1;
            }
            else
            {
                losses++;
            }

            if (teamGoals > 0 && opponentGoals > 0)
            {
                bttsHits++;
            }

            if (teamGoals + opponentGoals >= 3)
            {
                over25Hits++;
            }
            else
            {
                under25Hits++;
            }

            if (opponentGoals == 0)
            {
                cleanSheets++;
            }
        }

        var sample = Math.Max(results.Count, 1);
        if (venueOnly)
        {
            snapshot.VenueWins = wins;
            snapshot.VenueDraws = draws;
            snapshot.VenueLosses = losses;
            snapshot.VenueDrawRate = Math.Round(draws / (double)sample, 3);
            snapshot.VenuePointsPerMatch = Math.Round(points / sample, 2);
            snapshot.VenueGoalsForPerMatch = Math.Round(goalsFor / sample, 2);
            snapshot.VenueGoalsAgainstPerMatch = Math.Round(goalsAgainst / sample, 2);
            return;
        }

        snapshot.Wins = wins;
        snapshot.Draws = draws;
        snapshot.Losses = losses;
        snapshot.DrawRate = Math.Round(draws / (double)sample, 3);
        snapshot.PointsPerMatch = Math.Round(points / sample, 2);
        snapshot.GoalsForPerMatch = Math.Round(goalsFor / sample, 2);
        snapshot.GoalsAgainstPerMatch = Math.Round(goalsAgainst / sample, 2);
        snapshot.BttsRate = Math.Round(bttsHits / (double)sample, 3);
        snapshot.Over25Rate = Math.Round(over25Hits / (double)sample, 3);
        snapshot.Under25Rate = Math.Round(under25Hits / (double)sample, 3);
        snapshot.CleanSheetRate = Math.Round(cleanSheets / (double)sample, 3);
    }

    private static HeadToHeadSummary? BuildHeadToHeadSummary(
        IReadOnlyList<HistoricalFixtureResult> results,
        string homeTeam,
        string awayTeam)
    {
        if (results.Count == 0)
        {
            return null;
        }

        var summary = new HeadToHeadSummary
        {
            SampleSize = results.Count
        };

        foreach (var result in results)
        {
            if (result.HomeGoals > result.AwayGoals)
            {
                if (TeamMatches(result.HomeTeam, homeTeam))
                {
                    summary.HomeTeamWins++;
                }
                else
                {
                    summary.AwayTeamWins++;
                }
            }
            else if (result.HomeGoals < result.AwayGoals)
            {
                if (TeamMatches(result.AwayTeam, homeTeam))
                {
                    summary.HomeTeamWins++;
                }
                else
                {
                    summary.AwayTeamWins++;
                }
            }
            else
            {
                summary.Draws++;
            }

            if (result.HomeGoals > 0 && result.AwayGoals > 0)
            {
                summary.BttsRate += 1;
            }

            if (result.HomeGoals + result.AwayGoals >= 3)
            {
                summary.Over25Rate += 1;
            }

            summary.RecentScores.Add($"{result.HomeTeam} {result.HomeGoals}:{result.AwayGoals} {result.AwayTeam}");
        }

        summary.BttsRate = Math.Round(summary.BttsRate / results.Count, 3);
        summary.Over25Rate = Math.Round(summary.Over25Rate / results.Count, 3);
        return summary;
    }

    private static string ResolveDataQuality(TeamFormSnapshot homeForm, TeamFormSnapshot awayForm)
    {
        var highQuality =
            homeForm.SampleSize >= ResultWindow &&
            awayForm.SampleSize >= ResultWindow &&
            homeForm.VenueSampleSize >= 3 &&
            awayForm.VenueSampleSize >= 3;
        if (highQuality)
        {
            return "High";
        }

        var mediumQuality =
            homeForm.SampleSize >= OverallSampleThreshold &&
            awayForm.SampleSize >= OverallSampleThreshold;
        return mediumQuality ? "Medium" : "Low";
    }

    private static TeamFormMatchSummary ToMatchSummary(HistoricalFixtureResult result, string teamName)
    {
        var isHome = TeamMatches(result.HomeTeam, teamName);
        var opponent = isHome ? result.AwayTeam : result.HomeTeam;
        var (goalsFor, goalsAgainst) = ResolveScorePerspective(result, teamName);
        var resultCode = goalsFor > goalsAgainst ? "W" : goalsFor == goalsAgainst ? "D" : "L";

        return new TeamFormMatchSummary
        {
            MatchDate = result.MatchTimeUtc.ToString("dd MMM", CultureInfo.InvariantCulture),
            League = result.League,
            Venue = isHome ? "Home" : "Away",
            Opponent = opponent,
            Result = resultCode,
            Score = $"{goalsFor}:{goalsAgainst}"
        };
    }

    private static (int GoalsFor, int GoalsAgainst) ResolveScorePerspective(HistoricalFixtureResult result, string teamName)
    {
        return TeamMatches(result.HomeTeam, teamName)
            ? (result.HomeGoals, result.AwayGoals)
            : (result.AwayGoals, result.HomeGoals);
    }

    private static bool TeamPlayedAtVenue(HistoricalFixtureResult result, string teamName, bool isHome)
    {
        return isHome
            ? TeamMatches(result.HomeTeam, teamName)
            : TeamMatches(result.AwayTeam, teamName);
    }

    private static bool TeamsMatchFixture(HistoricalFixtureResult result, string homeTeam, string awayTeam)
    {
        return (TeamMatches(result.HomeTeam, homeTeam) && TeamMatches(result.AwayTeam, awayTeam)) ||
               (TeamMatches(result.HomeTeam, awayTeam) && TeamMatches(result.AwayTeam, homeTeam));
    }

    private static bool TeamMatches(string sourceTeamName, string targetTeamName)
    {
        return NormalizeTeamName(sourceTeamName) == NormalizeTeamName(targetTeamName);
    }

    private static string NormalizeTeamName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray();
        return new string(chars);
    }

    private static DateTime ResolveCutoffUtc(AiChatFootballInsightRequest request)
    {
        if (request.MatchDateTimeUtc.HasValue)
        {
            return request.MatchDateTimeUtc.Value;
        }

        if (TimeOnly.TryParseExact(
                request.KickoffTime,
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var kickoff))
        {
            var localKickoff = request.MatchLocalDate.ToDateTime(kickoff, DateTimeKind.Unspecified);
            return DateTimeProvider.ConvertLocalToUtc(localKickoff);
        }

        return DateTimeProvider.ConvertLocalToUtc(
            request.MatchLocalDate.ToDateTime(new TimeOnly(23, 59), DateTimeKind.Unspecified));
    }

    private static bool IsEligibleFootballRequest(AiChatFootballInsightRequest request)
    {
        return request.PredictionCategory is "BothTeamsScore" or "Over2.5Goals" or "Under2.5Goals" or "Draw" or "StraightWin";
    }

    private static bool TryParseScore(string? score, out int homeGoals, out int awayGoals)
    {
        homeGoals = 0;
        awayGoals = 0;
        if (string.IsNullOrWhiteSpace(score))
        {
            return false;
        }

        var parts = score.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 &&
               int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out homeGoals) &&
               int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out awayGoals);
    }

    private static string? ReadString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var part in path)
        {
            if (!current.TryGetProperty(part, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static int? ReadInt(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var part in path)
        {
            if (!current.TryGetProperty(part, out current))
            {
                return null;
            }
        }

        if (current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out var value))
        {
            return value;
        }

        return null;
    }

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
    }

    private static string BuildCacheKey(AiChatFootballInsightRequest request)
    {
        return string.Join(
            "|",
            "ai-football-insight",
            request.MatchLocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            NormalizeTeamName(request.League),
            NormalizeTeamName(request.HomeTeam),
            NormalizeTeamName(request.AwayTeam));
    }

    private sealed record HistoricalFixtureResult(
        string SourceName,
        int SourcePriority,
        string League,
        string HomeTeam,
        string AwayTeam,
        DateTime MatchTimeUtc,
        int HomeGoals,
        int AwayGoals)
    {
        public string DeduplicationKey => string.Join(
            "|",
            MatchTimeUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            NormalizeTeamName(HomeTeam),
            NormalizeTeamName(AwayTeam));
    }
}
