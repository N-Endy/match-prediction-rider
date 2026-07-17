using System.Globalization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Infrastructure.Services;

public static partial class SofaScoreEventPageParser
{
    public static bool TryParse(string html, string eventUrl, out SofaScoreMatchScore matchScore)
    {
        matchScore = new SofaScoreMatchScore
        {
            EventUrl = eventUrl,
            EventId = TryParseEventId(eventUrl)
        };

        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        var text = ExtractVisibleText(html);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var aboutMatch = AboutMatchRegex().Match(text);
        if (!aboutMatch.Success)
        {
            return false;
        }

        matchScore.HomeTeam = aboutMatch.Groups["home"].Value.Trim();
        matchScore.AwayTeam = aboutMatch.Groups["away"].Value.Trim();
        matchScore.MatchTime = ParseKickoffUtc(
            aboutMatch.Groups["day"].Value,
            aboutMatch.Groups["month"].Value,
            aboutMatch.Groups["year"].Value,
            aboutMatch.Groups["time"].Value);

        var leagueMatch = LeagueRegex().Match(text);
        if (leagueMatch.Success)
        {
            matchScore.League = leagueMatch.Groups["league"].Value.Trim();
        }

        matchScore.StatusText = ResolveStatusText(text);
        matchScore.HalfTimeScore = ParseScoreFromMarker(text, "HT");
        matchScore.RegularTimeScore = ParseScoreFromMarker(text, "FT");
        matchScore.ExtraTimeScore = ParseScoreFromMarker(text, "ET");
        matchScore.DisplayedScore = ResolveDisplayedScore(text, matchScore);
        matchScore.Score = matchScore.RegularTimeScore ??
                           matchScore.DisplayedScore ??
                           matchScore.ExtraTimeScore ??
                           string.Empty;

        if (string.IsNullOrWhiteSpace(matchScore.Score))
        {
            return false;
        }

        matchScore.BTTSLabel = ComputeBtts(matchScore.Score);
        matchScore.IsLive = DetermineLiveState(matchScore.StatusText, text);
        return true;
    }

    public static string ExtractVisibleText(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var removableNodes = doc.DocumentNode.SelectNodes("//script|//style|//noscript");
        if (removableNodes is not null)
        {
            foreach (var node in removableNodes)
            {
                node.Remove();
            }
        }

        var text = HtmlEntity.DeEntitize(doc.DocumentNode.InnerText ?? string.Empty) ?? string.Empty;
        text = WhitespaceRegex().Replace(text, " ").Trim();
        return text;
    }

    private static DateTime ParseKickoffUtc(string day, string month, string year, string time)
    {
        var raw = $"{day} {month} {year} {time}";
        return DateTime.ParseExact(
            raw,
            "d MMM yyyy HH:mm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    private static string? ResolveStatusText(string text)
    {
        foreach (var status in new[] { "After penalties", "After extra time", "Awarded", "Finished", "Canceled", "Postponed", "Interrupted", "Halftime" })
        {
            if (text.Contains(status, StringComparison.OrdinalIgnoreCase))
            {
                return status;
            }
        }

        var liveMinute = LiveMinuteRegex().Match(text);
        return liveMinute.Success ? liveMinute.Value : null;
    }

    private static string? ParseScoreFromMarker(string text, string marker)
    {
        var match = Regex.Match(
            text,
            $@"\b{Regex.Escape(marker)}\b\s+(?<home>\d+)\s*-\s*(?<away>\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return match.Success
            ? $"{match.Groups["home"].Value}:{match.Groups["away"].Value}"
            : null;
    }

    private static string? ResolveDisplayedScore(string text, SofaScoreMatchScore score)
    {
        if (!string.IsNullOrWhiteSpace(score.RegularTimeScore))
        {
            return score.RegularTimeScore;
        }

        if (!string.IsNullOrWhiteSpace(score.ExtraTimeScore) &&
            string.Equals(score.StatusText, "After extra time", StringComparison.OrdinalIgnoreCase))
        {
            return score.ExtraTimeScore;
        }

        var topScore = TopScoreRegex().Match(text);
        return topScore.Success
            ? $"{topScore.Groups["home"].Value}:{topScore.Groups["away"].Value}"
            : null;
    }

    private static bool DetermineLiveState(string? statusText, string text)
    {
        if (string.IsNullOrWhiteSpace(statusText))
        {
            return LiveMinuteRegex().IsMatch(text);
        }

        if (statusText.Contains("After", StringComparison.OrdinalIgnoreCase) ||
            statusText.Contains("Finished", StringComparison.OrdinalIgnoreCase) ||
            statusText.Contains("Awarded", StringComparison.OrdinalIgnoreCase) ||
            statusText.Contains("Canceled", StringComparison.OrdinalIgnoreCase) ||
            statusText.Contains("Postponed", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool ComputeBtts(string score)
    {
        var parts = score.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 &&
               int.TryParse(parts[0], out var home) &&
               int.TryParse(parts[1], out var away) &&
               home > 0 &&
               away > 0;
    }

    private static long? TryParseEventId(string? eventUrl)
    {
        if (string.IsNullOrWhiteSpace(eventUrl))
        {
            return null;
        }

        var match = EventIdRegex().Match(eventUrl);
        return match.Success && long.TryParse(match.Groups["id"].Value, out var eventId)
            ? eventId
            : null;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"(?:/event/|/api/v1/event/)(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EventIdRegex();

    [GeneratedRegex(@"(?<home>.+?)\s+is going head to head with\s+(?<away>.+?)\s+starting on\s+(?<day>\d{1,2})\s+(?<month>[A-Za-z]{3})\s+(?<year>\d{4})\s+at\s+(?<time>\d{2}:\d{2})\s+UTC", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AboutMatchRegex();

    [GeneratedRegex(@"The match is a part of the\s+(?<league>.+?)\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeagueRegex();

    [GeneratedRegex(@"(?<!\d)(?<minute>\d{1,3}(?:\+\d{1,2})?')", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LiveMinuteRegex();

    [GeneratedRegex(@"\b(?<home>\d+)\s*-\s*(?<away>\d+)\b")]
    private static partial Regex TopScoreRegex();
}
