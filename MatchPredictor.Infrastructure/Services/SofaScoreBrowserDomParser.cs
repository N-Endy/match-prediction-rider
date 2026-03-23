using System.Text.Json;
using System.Text.RegularExpressions;

namespace MatchPredictor.Infrastructure.Services;

public static partial class SofaScoreBrowserDomParser
{
    public static IReadOnlyList<SofaScoreRenderedDomCandidate> ParseCandidates(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<SofaScoreRenderedDomCandidate>>(
                       json,
                       new JsonSerializerOptions
                       {
                           PropertyNameCaseInsensitive = true
                       })?
                   .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Href) &&
                                       !string.IsNullOrWhiteSpace(candidate.Text))
                   .ToList()
                   ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static IReadOnlyList<SofaScoreListingEntry> ParseEntries(
        IEnumerable<SofaScoreRenderedDomCandidate> candidates,
        string baseUrl)
    {
        var entries = new List<SofaScoreListingEntry>();

        foreach (var candidate in candidates)
        {
            if (TryParseCandidate(candidate, baseUrl, out var entry))
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    private static bool TryParseCandidate(
        SofaScoreRenderedDomCandidate candidate,
        string baseUrl,
        out SofaScoreListingEntry entry)
    {
        entry = new SofaScoreListingEntry();

        var lines = SplitLines(candidate.Text);
        if (lines.Count == 0)
        {
            return false;
        }

        var teamNames = ExtractTeamNames(lines);
        if (teamNames.Count < 2)
        {
            teamNames = ExtractTeamNamesFromTitle(candidate.Title);
        }

        if (teamNames.Count < 2 ||
            !TryExtractScore(lines, out var homeScore, out var awayScore))
        {
            return false;
        }

        var kickoff = lines
            .Select(ParseKickoff)
            .FirstOrDefault(value => value.HasValue);

        var status = lines.FirstOrDefault(LooksLikeStatusLine);

        entry = new SofaScoreListingEntry
        {
            League = ExtractLeague(candidate.SectionText, teamNames),
            HomeTeam = teamNames[0],
            AwayTeam = teamNames[1],
            Score = $"{homeScore}:{awayScore}",
            StatusText = status,
            EventUrl = NormalizeEventUrl(candidate.Href, baseUrl),
            KickoffLocalTime = kickoff,
            IsLive = DetermineLiveState(status, lines)
        };

        return true;
    }

    private static List<string> SplitLines(string? rawText)
    {
        return (rawText ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => RegexWhitespace().Replace(line, " ").Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static List<string> ExtractTeamNames(IReadOnlyList<string> lines)
    {
        return lines
            .Where(LooksLikeTeamLine)
            .Take(2)
            .ToList();
    }

    private static List<string> ExtractTeamNamesFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return [];
        }

        var sanitized = RegexWhitespace().Replace(title, " ").Trim();
        var liveScoreIndex = sanitized.IndexOf(" live score", StringComparison.OrdinalIgnoreCase);
        if (liveScoreIndex <= 0)
        {
            return [];
        }

        sanitized = sanitized[..liveScoreIndex].Trim();
        var vsMatch = VersusTitleRegex().Match(sanitized);
        if (!vsMatch.Success)
        {
            return [];
        }

        var home = vsMatch.Groups["home"].Value.Trim();
        var away = vsMatch.Groups["away"].Value.Trim();
        return string.IsNullOrWhiteSpace(home) || string.IsNullOrWhiteSpace(away)
            ? []
            : [home, away];
    }

    private static bool TryExtractScore(
        IReadOnlyList<string> lines,
        out int homeScore,
        out int awayScore)
    {
        homeScore = 0;
        awayScore = 0;

        foreach (var line in lines)
        {
            var pairMatch = ScorePairRegex().Match(line);
            if (pairMatch.Success &&
                int.TryParse(pairMatch.Groups["home"].Value, out homeScore) &&
                int.TryParse(pairMatch.Groups["away"].Value, out awayScore))
            {
                return true;
            }
        }

        var scoreDigits = lines
            .Where(line => SingleDigitRegex().IsMatch(line))
            .Select(line => int.Parse(line))
            .Where(value => value is >= 0 and <= 5)
            .Take(2)
            .ToList();

        if (scoreDigits.Count < 2)
        {
            return false;
        }

        homeScore = scoreDigits[0];
        awayScore = scoreDigits[1];
        return true;
    }

    private static TimeOnly? ParseKickoff(string line)
    {
        return KickoffRegex().IsMatch(line)
            ? TimeOnly.ParseExact(line, "HH:mm")
            : null;
    }

    private static bool LooksLikeTeamLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) ||
            line.Length < 3 ||
            KickoffRegex().IsMatch(line) ||
            LooksLikeStatusLine(line) ||
            ScorePairRegex().IsMatch(line) ||
            SingleDigitRegex().IsMatch(line))
        {
            return false;
        }

        return line.Any(char.IsLetter);
    }

    private static bool LooksLikeStatusLine(string line)
    {
        return LiveMinuteRegex().IsMatch(line) ||
               PhaseRegex().IsMatch(line) ||
               line.Contains("finished", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("cancel", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("postpon", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("interrupt", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("retired", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("walkover", StringComparison.OrdinalIgnoreCase);
    }

    private static bool DetermineLiveState(string? status, IReadOnlyList<string> lines)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return lines.Any(line => line.Contains("live", StringComparison.OrdinalIgnoreCase));
        }

        return LiveMinuteRegex().IsMatch(status) ||
               status.StartsWith("set ", StringComparison.OrdinalIgnoreCase) ||
               status.StartsWith("game ", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "live", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractLeague(string? sectionText, IReadOnlyList<string> teamNames)
    {
        if (string.IsNullOrWhiteSpace(sectionText))
        {
            return string.Empty;
        }

        var lines = SplitLines(sectionText);
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        return lines.FirstOrDefault(line =>
                   !teamNames.Contains(line, StringComparer.OrdinalIgnoreCase) &&
                   !KickoffRegex().IsMatch(line) &&
                   !LooksLikeStatusLine(line) &&
                   !ScorePairRegex().IsMatch(line) &&
                   !SingleDigitRegex().IsMatch(line))
               ?? string.Empty;
    }

    private static string NormalizeEventUrl(string href, string baseUrl)
    {
        var absolute = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? href
            : $"{baseUrl.TrimEnd('/')}/{href.TrimStart('/')}";
        return SofaScoreDiscoveryHelper.NormalizeMatchUrl(absolute);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex RegexWhitespace();

    [GeneratedRegex(@"^\d{2}:\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex KickoffRegex();

    [GeneratedRegex(@"^(?<home>\d+)\s*[-:]\s*(?<away>\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ScorePairRegex();

    [GeneratedRegex(@"^\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex SingleDigitRegex();

    [GeneratedRegex(@"^\d{1,3}(?:\+\d{1,2})?'$", RegexOptions.CultureInvariant)]
    private static partial Regex LiveMinuteRegex();

    [GeneratedRegex(@"^(?:1ST|2ND|3RD|4TH|5TH|HT|FT|ET|AET|PEN|LIVE|SET\s+\d+|GAME\s+\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PhaseRegex();

    [GeneratedRegex(@"^(?<home>.+?)\s+v(?:s)?\.?\s+(?<away>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersusTitleRegex();
}

public sealed class SofaScoreRenderedDomCandidate
{
    public string Href { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public string? SectionText { get; init; }
    public string? Title { get; init; }
}
