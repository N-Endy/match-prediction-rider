using System.Text.Json;
using System.Text.RegularExpressions;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Infrastructure.Services;

public static partial class SofaScoreBrowserFetchParser
{
    public static IReadOnlyList<SofaScoreBrowserFetchResponse> ParseResponses(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<SofaScoreBrowserFetchResponse>>(
                       json,
                       new JsonSerializerOptions
                       {
                           PropertyNameCaseInsensitive = true
                       })?
                   .Where(response => !string.IsNullOrWhiteSpace(response.RelativePath))
                   .ToList()
                   ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void ParseEventSummaries(
        IEnumerable<SofaScoreBrowserFetchResponse> responses,
        string baseUrl,
        out List<SofaScoreApiEventSummary> liveEvents,
        out List<SofaScoreApiEventSummary> scheduledEvents)
    {
        liveEvents = [];
        scheduledEvents = [];

        foreach (var response in responses.Where(response => response.Ok && !string.IsNullOrWhiteSpace(response.Body)))
        {
            IReadOnlyList<SofaScoreApiEventSummary> parsed;
            try
            {
                parsed = SofaScoreApiParser.ParseEventSummaries(response.Body!, baseUrl);
            }
            catch
            {
                continue;
            }

            if (response.RelativePath.Contains("/events/live", StringComparison.OrdinalIgnoreCase))
            {
                liveEvents.AddRange(parsed);
                continue;
            }

            if (response.RelativePath.Contains("/scheduled-events/", StringComparison.OrdinalIgnoreCase))
            {
                scheduledEvents.AddRange(parsed);
            }
        }
    }

    public static Dictionary<long, SofaScoreMatchScore> ParseEventDetails(
        IEnumerable<SofaScoreBrowserFetchResponse> responses,
        string baseUrl)
    {
        var parsedDetails = new Dictionary<long, SofaScoreMatchScore>();

        foreach (var response in responses.Where(response => response.Ok && !string.IsNullOrWhiteSpace(response.Body)))
        {
            var eventId = ExtractEventId(response.RelativePath);
            if (!eventId.HasValue)
            {
                continue;
            }

            try
            {
                if (SofaScoreApiParser.TryParseEventDetail(response.Body!, baseUrl, out var detailScore))
                {
                    parsedDetails[eventId.Value] = detailScore;
                }
            }
            catch
            {
                // Ignore malformed detail payloads and keep the rest.
            }
        }

        return parsedDetails;
    }

    private static long? ExtractEventId(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var match = EventIdRegex().Match(relativePath);
        return match.Success && long.TryParse(match.Groups["id"].Value, out var eventId)
            ? eventId
            : null;
    }

    [GeneratedRegex(@"/api/v1/event/(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex EventIdRegex();
}

public sealed class SofaScoreBrowserFetchResponse
{
    public string RelativePath { get; init; } = string.Empty;
    public bool Ok { get; init; }
    public int Status { get; init; }
    public string? Body { get; init; }
    public string? Error { get; init; }
}
