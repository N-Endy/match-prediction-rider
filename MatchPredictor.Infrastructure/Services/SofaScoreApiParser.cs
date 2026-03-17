using System.Text.Json;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Infrastructure.Services;

public static class SofaScoreApiParser
{
    public static IReadOnlyList<SofaScoreApiEventSummary> ParseEventSummaries(string json, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("events", out var eventsElement) ||
            eventsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var summaries = new List<SofaScoreApiEventSummary>();
        foreach (var eventElement in eventsElement.EnumerateArray())
        {
            if (!TryParseEventSummary(eventElement, baseUrl, out var summary))
            {
                continue;
            }

            summaries.Add(summary);
        }

        return summaries;
    }

    public static bool TryParseEventDetail(string json, string baseUrl, out SofaScoreMatchScore matchScore)
    {
        matchScore = new SofaScoreMatchScore();
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var eventElement = root.TryGetProperty("event", out var wrappedEvent)
            ? wrappedEvent
            : root;

        return TryBuildMatchScore(eventElement, baseUrl, out matchScore);
    }

    private static bool TryParseEventSummary(JsonElement eventElement, string baseUrl, out SofaScoreApiEventSummary summary)
    {
        summary = new SofaScoreApiEventSummary();
        if (!TryBuildMatchScore(eventElement, baseUrl, out var matchScore))
        {
            return false;
        }

        var statusType = ReadString(eventElement, "status", "type");
        if (!IsRelevantStatus(statusType, matchScore.IsLive))
        {
            return false;
        }

        summary = new SofaScoreApiEventSummary
        {
            EventId = ReadLong(eventElement, "id") ?? 0,
            League = matchScore.League,
            HomeTeam = matchScore.HomeTeam,
            AwayTeam = matchScore.AwayTeam,
            Score = matchScore.Score,
            DisplayedScore = matchScore.DisplayedScore,
            RegularTimeScore = matchScore.RegularTimeScore,
            HalfTimeScore = matchScore.HalfTimeScore,
            ExtraTimeScore = matchScore.ExtraTimeScore,
            StatusText = matchScore.StatusText,
            EventUrl = matchScore.EventUrl,
            MatchTime = matchScore.MatchTime,
            BTTSLabel = matchScore.BTTSLabel,
            IsLive = matchScore.IsLive
        };

        return summary.EventId > 0;
    }

    private static bool TryBuildMatchScore(JsonElement eventElement, string baseUrl, out SofaScoreMatchScore matchScore)
    {
        matchScore = new SofaScoreMatchScore();

        var eventId = ReadLong(eventElement, "id");
        var homeTeam = ReadString(eventElement, "homeTeam", "name");
        var awayTeam = ReadString(eventElement, "awayTeam", "name");
        if (string.IsNullOrWhiteSpace(homeTeam) || string.IsNullOrWhiteSpace(awayTeam))
        {
            return false;
        }

        var statusType = ReadString(eventElement, "status", "type");
        var statusDescription = ReadString(eventElement, "status", "description")
            ?? ReadString(eventElement, "status", "code")
            ?? statusType
            ?? string.Empty;

        var isLive = IsLiveStatus(statusType, statusDescription);
        var league = BuildLeagueLabel(eventElement);
        var matchTime = ResolveMatchTimeUtc(eventElement);
        var homeScoreElement = TryGetObject(eventElement, "homeScore");
        var awayScoreElement = TryGetObject(eventElement, "awayScore");
        var scoreBundle = BuildScoreBundle(homeScoreElement, awayScoreElement, statusDescription, isLive);

        if (string.IsNullOrWhiteSpace(scoreBundle.SettlementScore))
        {
            return false;
        }

        matchScore = new SofaScoreMatchScore
        {
            League = league,
            HomeTeam = homeTeam,
            AwayTeam = awayTeam,
            Score = scoreBundle.SettlementScore,
            DisplayedScore = scoreBundle.DisplayedScore,
            RegularTimeScore = scoreBundle.RegularTimeScore,
            HalfTimeScore = scoreBundle.HalfTimeScore,
            ExtraTimeScore = scoreBundle.ExtraTimeScore,
            StatusText = statusDescription,
            EventUrl = eventId.HasValue && eventId.Value > 0
                ? $"{baseUrl.TrimEnd('/')}/api/v1/event/{eventId.Value}"
                : string.Empty,
            MatchTime = matchTime,
            BTTSLabel = scoreBundle.SettlementScore.Split(':') is [var home, var away] &&
                        int.TryParse(home, out var homeGoals) &&
                        int.TryParse(away, out var awayGoals) &&
                        homeGoals > 0 &&
                        awayGoals > 0,
            IsLive = isLive
        };

        return true;
    }

    private static SofaScoreScoreBundle BuildScoreBundle(
        JsonElement? homeScoreElement,
        JsonElement? awayScoreElement,
        string statusDescription,
        bool isLive)
    {
        var home = ExtractScoreLine(homeScoreElement);
        var away = ExtractScoreLine(awayScoreElement);

        var regularHome = home.NormalTime ?? home.Display ?? home.Current;
        var regularAway = away.NormalTime ?? away.Display ?? away.Current;
        var liveHome = home.Display ?? home.Current ?? regularHome;
        var liveAway = away.Display ?? away.Current ?? regularAway;
        var settlementHome = isLive ? liveHome : ResolveFinishedSettlementScore(home, regularHome, statusDescription);
        var settlementAway = isLive ? liveAway : ResolveFinishedSettlementScore(away, regularAway, statusDescription);
        var displayedHome = liveHome ?? settlementHome;
        var displayedAway = liveAway ?? settlementAway;
        var halfTimeHome = home.Period1;
        var halfTimeAway = away.Period1;
        var extraTimeHome = ResolveExtraScore(home, regularHome, displayedHome, statusDescription, isLive);
        var extraTimeAway = ResolveExtraScore(away, regularAway, displayedAway, statusDescription, isLive);

        return new SofaScoreScoreBundle
        {
            SettlementScore = ToScore(settlementHome, settlementAway),
            DisplayedScore = ToScore(displayedHome, displayedAway),
            RegularTimeScore = ToScore(regularHome, regularAway),
            HalfTimeScore = ToScore(halfTimeHome, halfTimeAway),
            ExtraTimeScore = ToScore(extraTimeHome, extraTimeAway)
        };
    }

    private static SofaScoreScoreLine ExtractScoreLine(JsonElement? scoreElement)
    {
        if (scoreElement is null || scoreElement.Value.ValueKind != JsonValueKind.Object)
        {
            return new SofaScoreScoreLine();
        }

        var element = scoreElement.Value;
        return new SofaScoreScoreLine
        {
            Current = ReadInt(element, "current"),
            Display = ReadInt(element, "display"),
            NormalTime = ReadInt(element, "normaltime") ?? ReadInt(element, "normalTime"),
            Period1 = ReadInt(element, "period1"),
            Penalties = ReadInt(element, "penalties"),
            Overtime = ReadInt(element, "overtime")
        };
    }

    private static int? ResolveFinishedSettlementScore(SofaScoreScoreLine line, int? regularScore, string statusDescription)
    {
        if (regularScore.HasValue && IndicatesExtendedResult(statusDescription, line))
        {
            return regularScore;
        }

        return line.Display ?? line.Current ?? regularScore;
    }

    private static int? ResolveExtraScore(
        SofaScoreScoreLine line,
        int? regularScore,
        int? displayedScore,
        string statusDescription,
        bool isLive)
    {
        if (isLive)
        {
            return null;
        }

        if (line.Overtime.HasValue)
        {
            return line.Overtime.Value;
        }

        if (line.Penalties.HasValue)
        {
            return line.Penalties.Value;
        }

        if (IndicatesExtendedResult(statusDescription, line) &&
            line.Current.HasValue &&
            regularScore.HasValue &&
            line.Current.Value != regularScore.Value)
        {
            return line.Current.Value;
        }

        if (IndicatesExtendedResult(statusDescription, line) &&
            displayedScore.HasValue &&
            regularScore.HasValue &&
            displayedScore.Value != regularScore.Value)
        {
            return displayedScore.Value;
        }

        return null;
    }

    private static bool IndicatesExtendedResult(string statusDescription, SofaScoreScoreLine line)
    {
        return statusDescription.Contains("extra", StringComparison.OrdinalIgnoreCase) ||
               statusDescription.Contains("pen", StringComparison.OrdinalIgnoreCase) ||
               line.Penalties.HasValue ||
               line.Overtime.HasValue;
    }

    private static bool IsRelevantStatus(string? statusType, bool isLive)
    {
        if (isLive)
        {
            return true;
        }

        return string.Equals(statusType, "finished", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLiveStatus(string? statusType, string? statusDescription)
    {
        if (string.Equals(statusType, "inprogress", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(statusDescription))
        {
            return false;
        }

        return statusDescription.Contains("'", StringComparison.Ordinal) ||
               statusDescription.Contains("HT", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildLeagueLabel(JsonElement eventElement)
    {
        var tournamentName = ReadString(eventElement, "tournament", "name") ?? string.Empty;
        var categoryName = ReadString(eventElement, "tournament", "category", "name") ?? string.Empty;

        if (string.IsNullOrWhiteSpace(categoryName))
        {
            return tournamentName;
        }

        if (string.IsNullOrWhiteSpace(tournamentName) ||
            tournamentName.Contains(categoryName, StringComparison.OrdinalIgnoreCase))
        {
            return tournamentName;
        }

        return $"{categoryName} - {tournamentName}";
    }

    private static DateTime ResolveMatchTimeUtc(JsonElement eventElement)
    {
        var startTimestamp = ReadLong(eventElement, "startTimestamp");
        return startTimestamp.HasValue
            ? DateTimeOffset.FromUnixTimeSeconds(startTimestamp.Value).UtcDateTime
            : DateTime.UtcNow;
    }

    private static string? ToScore(int? home, int? away)
    {
        return home.HasValue && away.HasValue
            ? $"{home.Value}:{away.Value}"
            : null;
    }

    private static JsonElement? TryGetObject(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current;
    }

    private static string? ReadString(JsonElement element, params string[] path)
    {
        var resolved = TryGetObject(element, path);
        if (resolved is null)
        {
            return null;
        }

        return resolved.Value.ValueKind switch
        {
            JsonValueKind.String => resolved.Value.GetString(),
            JsonValueKind.Number => resolved.Value.ToString(),
            _ => null
        };
    }

    private static int? ReadInt(JsonElement element, params string[] path)
    {
        var resolved = TryGetObject(element, path);
        if (resolved is null)
        {
            return null;
        }

        if (resolved.Value.ValueKind == JsonValueKind.Number && resolved.Value.TryGetInt32(out var numeric))
        {
            return numeric;
        }

        if (resolved.Value.ValueKind == JsonValueKind.String &&
            int.TryParse(resolved.Value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static long? ReadLong(JsonElement element, params string[] path)
    {
        var resolved = TryGetObject(element, path);
        if (resolved is null)
        {
            return null;
        }

        if (resolved.Value.ValueKind == JsonValueKind.Number && resolved.Value.TryGetInt64(out var numeric))
        {
            return numeric;
        }

        if (resolved.Value.ValueKind == JsonValueKind.String &&
            long.TryParse(resolved.Value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private sealed record SofaScoreScoreBundle
    {
        public string? SettlementScore { get; init; }
        public string? DisplayedScore { get; init; }
        public string? RegularTimeScore { get; init; }
        public string? HalfTimeScore { get; init; }
        public string? ExtraTimeScore { get; init; }
    }

    private sealed record SofaScoreScoreLine
    {
        public int? Current { get; init; }
        public int? Display { get; init; }
        public int? NormalTime { get; init; }
        public int? Period1 { get; init; }
        public int? Penalties { get; init; }
        public int? Overtime { get; init; }
    }
}

public sealed class SofaScoreApiEventSummary
{
    public long EventId { get; init; }
    public string League { get; init; } = string.Empty;
    public string HomeTeam { get; init; } = string.Empty;
    public string AwayTeam { get; init; } = string.Empty;
    public string Score { get; init; } = string.Empty;
    public string? DisplayedScore { get; init; }
    public string? RegularTimeScore { get; init; }
    public string? HalfTimeScore { get; init; }
    public string? ExtraTimeScore { get; init; }
    public string? StatusText { get; init; }
    public string EventUrl { get; init; } = string.Empty;
    public DateTime MatchTime { get; init; }
    public bool BTTSLabel { get; init; }
    public bool IsLive { get; init; }

    public SofaScoreMatchScore ToMatchScore()
    {
        return new SofaScoreMatchScore
        {
            League = League,
            HomeTeam = HomeTeam,
            AwayTeam = AwayTeam,
            Score = Score,
            DisplayedScore = DisplayedScore,
            RegularTimeScore = RegularTimeScore,
            HalfTimeScore = HalfTimeScore,
            ExtraTimeScore = ExtraTimeScore,
            StatusText = StatusText,
            EventUrl = EventUrl,
            MatchTime = MatchTime,
            BTTSLabel = BTTSLabel,
            IsLive = IsLive
        };
    }
}
