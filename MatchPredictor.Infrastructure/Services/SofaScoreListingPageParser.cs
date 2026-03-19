using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace MatchPredictor.Infrastructure.Services;

public static partial class SofaScoreListingPageParser
{
    public static IReadOnlyList<SofaScoreListingEntry> ParseEntries(string html, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return [];
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var entries = new List<SofaScoreListingEntry>();
        var normalizedBaseUrl = baseUrl.TrimEnd('/');
        var sections = doc.DocumentNode.SelectNodes("//div[contains(concat(' ', normalize-space(@class), ' '), ' pb_sm ')]");

        if (sections is not null)
        {
            foreach (var section in sections)
            {
                var leagueLabel = BuildLeagueLabel(section);
                ParseSectionEvents(section, leagueLabel, normalizedBaseUrl, entries);
            }
        }

        if (entries.Count > 0)
        {
            return entries;
        }

        ParseSectionEvents(doc.DocumentNode, string.Empty, normalizedBaseUrl, entries);
        return entries;
    }

    private static void ParseSectionEvents(HtmlNode sectionNode, string leagueLabel, string baseUrl, ICollection<SofaScoreListingEntry> entries)
    {
        var eventNodes = sectionNode.SelectNodes(".//a[@data-id and contains(@href, '/football/match/')]");
        if (eventNodes is null)
        {
            return;
        }

        foreach (var eventNode in eventNodes)
        {
            if (TryParseEvent(eventNode, leagueLabel, baseUrl, out var entry))
            {
                entries.Add(entry);
            }
        }
    }

    private static bool TryParseEvent(HtmlNode eventNode, string leagueLabel, string baseUrl, out SofaScoreListingEntry entry)
    {
        entry = new SofaScoreListingEntry();

        var href = eventNode.GetAttributeValue("href", string.Empty);
        if (string.IsNullOrWhiteSpace(href))
        {
            return false;
        }

        var teamContainer = FindTeamContainer(eventNode);
        if (teamContainer is null)
        {
            return false;
        }

        var teamNames = ExtractTeamNames(teamContainer);
        if (teamNames.Count < 2)
        {
            return false;
        }

        var statusBlock = FindStatusBlock(eventNode);
        var statusBlockTitle = statusBlock?.GetAttributeValue("title", string.Empty);
        var statusBlockTexts = statusBlock?.Descendants("bdi")
            .Select(ReadText)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList() ?? [];

        var kickoffText = statusBlockTexts.FirstOrDefault(text => KickoffRegex().IsMatch(text));
        var kickoffLocalTime = ParseKickoffLocalTime(kickoffText);

        var liveMinuteText = statusBlockTexts.FirstOrDefault(text => LiveMinuteRegex().IsMatch(text));
        var phaseText = statusBlockTexts.FirstOrDefault(text => PhaseRegex().IsMatch(text));
        var statusText = !string.IsNullOrWhiteSpace(liveMinuteText)
            ? liveMinuteText
            : !string.IsNullOrWhiteSpace(phaseText)
                ? phaseText
                : statusBlockTitle;

        var scoreContainer = FindScoreContainer(eventNode);
        if (scoreContainer is null)
        {
            return false;
        }

        if (!TryExtractTeamScores(scoreContainer, out var homeScore, out var awayScore))
        {
            return false;
        }

        entry = new SofaScoreListingEntry
        {
            League = leagueLabel,
            HomeTeam = teamNames[0],
            AwayTeam = teamNames[1],
            Score = $"{homeScore}:{awayScore}",
            StatusText = string.IsNullOrWhiteSpace(statusText) ? null : statusText.Trim(),
            EventUrl = NormalizeEventUrl(href, baseUrl),
            KickoffLocalTime = kickoffLocalTime,
            IsLive = DetermineLiveState(statusText, statusBlockTitle, scoreContainer.GetAttributeValue("class", string.Empty))
        };

        return true;
    }

    private static HtmlNode? FindTeamContainer(HtmlNode eventNode)
    {
        return eventNode.Descendants("div")
            .FirstOrDefault(node =>
            {
                var title = node.GetAttributeValue("title", string.Empty);
                if (!string.IsNullOrWhiteSpace(title) &&
                    title.Contains("live score", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return HasAllClassTokens(node, "ov_hidden", "min-w_[0px]") &&
                       node.Descendants("bdi").Count(child => !string.IsNullOrWhiteSpace(ReadText(child))) >= 2;
            });
    }

    private static List<string> ExtractTeamNames(HtmlNode teamContainer)
    {
        var rowNames = teamContainer.Elements("div")
            .Select(row => row.Descendants("bdi")
                .Select(ReadText)
                .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!)
            .Take(2)
            .ToList();

        if (rowNames.Count >= 2)
        {
            return rowNames;
        }

        return teamContainer.Descendants("bdi")
            .Select(ReadText)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Take(2)
            .ToList();
    }

    private static HtmlNode? FindStatusBlock(HtmlNode eventNode)
    {
        return eventNode.Descendants("div")
            .FirstOrDefault(node =>
            {
                var title = node.GetAttributeValue("title", string.Empty);
                if (!string.IsNullOrWhiteSpace(title) &&
                    !title.Contains("live score", StringComparison.OrdinalIgnoreCase) &&
                    HasClassToken(node, "ta_center"))
                {
                    return true;
                }

                return HasAllClassTokens(node, "ta_center", "flex-b_5xl");
            });
    }

    private static HtmlNode? FindScoreContainer(HtmlNode eventNode)
    {
        return eventNode.Descendants("div")
            .OrderByDescending(node => CountNumericScoreSpans(node))
            .FirstOrDefault(node =>
            {
                if (!HasAllClassTokens(node, "d_flex", "flex-d_column", "ai_flex-end", "jc_flex-end"))
                {
                    return false;
                }

                var scoreRows = node.Elements("div")
                    .Count(row => ExtractPrimaryScore(row).HasValue);
                return scoreRows >= 2;
            });
    }

    private static bool TryExtractTeamScores(HtmlNode scoreContainer, out int homeScore, out int awayScore)
    {
        homeScore = 0;
        awayScore = 0;

        var rowScores = scoreContainer.Elements("div")
            .Select(ExtractNumericScores)
            .Where(scores => scores.Count > 0)
            .Take(2)
            .ToList();

        if (rowScores.Count >= 2)
        {
            homeScore = rowScores[0][0];
            awayScore = rowScores[1][0];
            return true;
        }

        var flattenedScores = ExtractNumericScores(scoreContainer);
        if (flattenedScores.Count >= 2)
        {
            homeScore = flattenedScores[0];
            awayScore = flattenedScores[1];
            return true;
        }

        return false;
    }

    private static List<int> ExtractNumericScores(HtmlNode node)
    {
        return node.Descendants("span")
            .Where(span => HasClassToken(span, "score"))
            .Select(ReadText)
            .Select(text => text.Trim())
            .Where(text => NumericScoreRegex().IsMatch(text))
            .Select(int.Parse)
            .ToList();
    }

    private static int CountNumericScoreSpans(HtmlNode node)
    {
        return node.Descendants("span")
            .Count(span => HasClassToken(span, "score") && NumericScoreRegex().IsMatch(ReadText(span).Trim()));
    }

    private static int? ExtractPrimaryScore(HtmlNode rowNode)
    {
        var numericScores = ExtractNumericScores(rowNode);
        return numericScores.Count > 0 ? numericScores[0] : null;
    }

    private static string BuildLeagueLabel(HtmlNode sectionNode)
    {
        var leagueHeader = sectionNode.Descendants("div")
            .FirstOrDefault(node => HasAllClassTokens(node, "d_flex", "flex-d_column", "jc_center", "ov_hidden"));

        var league = ReadText(
            leagueHeader?.SelectSingleNode(".//a[contains(@href, '/football/tournament/')]//bdi[normalize-space()][1]") ??
            sectionNode.SelectSingleNode(".//a[contains(@href, '/football/tournament/')]//bdi[normalize-space()][1]"));

        var country = ReadText(
            leagueHeader?.SelectSingleNode(".//a[starts-with(@href, '/football/') and not(contains(@href, '/football/tournament/')) and not(contains(@href, '/football/match/'))]//bdi[normalize-space()][1]") ??
            sectionNode.SelectSingleNode(".//a[starts-with(@href, '/football/') and not(contains(@href, '/football/tournament/')) and not(contains(@href, '/football/match/'))]//bdi[normalize-space()][1]"));

        if (string.IsNullOrWhiteSpace(league))
        {
            return country;
        }

        if (string.IsNullOrWhiteSpace(country) ||
            league.Contains(country, StringComparison.OrdinalIgnoreCase))
        {
            return league;
        }

        return $"{country} - {league}";
    }

    private static bool DetermineLiveState(string? statusText, string? statusTitle, string? scoreContainerClass)
    {
        if (LiveMinuteRegex().IsMatch(statusText ?? string.Empty))
        {
            return true;
        }

        var combined = string.Join(
            " ",
            new[] { statusText, statusTitle, scoreContainerClass }
                .Where(value => !string.IsNullOrWhiteSpace(value)));

        if (string.IsNullOrWhiteSpace(combined))
        {
            return false;
        }

        return combined.Contains("1ST", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("2ND", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("HT", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("LIVE", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("c_status.live", StringComparison.OrdinalIgnoreCase);
    }

    private static TimeOnly? ParseKickoffLocalTime(string? kickoffText)
    {
        if (string.IsNullOrWhiteSpace(kickoffText))
        {
            return null;
        }

        return TimeOnly.TryParseExact(kickoffText.Trim(), "HH:mm", out var parsed)
            ? parsed
            : null;
    }

    private static bool HasClassToken(HtmlNode node, string classToken)
    {
        var classValue = node.GetAttributeValue("class", string.Empty);
        if (string.IsNullOrWhiteSpace(classValue))
        {
            return false;
        }

        return classValue
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(token => string.Equals(token, classToken, StringComparison.Ordinal));
    }

    private static bool HasAllClassTokens(HtmlNode node, params string[] classTokens)
    {
        return classTokens.All(classToken => HasClassToken(node, classToken));
    }

    private static string NormalizeEventUrl(string href, string baseUrl)
    {
        var absolute = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? href
            : $"{baseUrl}{href}";
        return SofaScoreDiscoveryHelper.NormalizeMatchUrl(absolute);
    }

    private static string ReadText(HtmlNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        var innerText = node.InnerText;
        var decodedText = HtmlEntity.DeEntitize(innerText ?? string.Empty);
        return (decodedText ?? string.Empty).Trim();
    }

    [GeneratedRegex(@"^\d{1,2}:\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex KickoffRegex();

    [GeneratedRegex(@"^\d{1,3}(?:\+\d{1,2})?'$", RegexOptions.CultureInvariant)]
    private static partial Regex LiveMinuteRegex();

    [GeneratedRegex(@"^(?:1ST|2ND|HT|FT|ET|AET|PEN|LIVE)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PhaseRegex();

    [GeneratedRegex(@"^\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericScoreRegex();
}

public sealed class SofaScoreListingEntry
{
    public string League { get; init; } = string.Empty;
    public string HomeTeam { get; init; } = string.Empty;
    public string AwayTeam { get; init; } = string.Empty;
    public string Score { get; init; } = string.Empty;
    public string? StatusText { get; init; }
    public string EventUrl { get; init; } = string.Empty;
    public TimeOnly? KickoffLocalTime { get; init; }
    public bool IsLive { get; init; }
}
