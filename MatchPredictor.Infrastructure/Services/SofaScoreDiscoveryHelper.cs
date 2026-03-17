using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace MatchPredictor.Infrastructure.Services;

public static class SofaScoreDiscoveryHelper
{
    public static IReadOnlyList<string> ParseRobotSitemapUrls(string robotsText)
    {
        if (string.IsNullOrWhiteSpace(robotsText))
        {
            return [];
        }

        return robotsText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("Sitemap:", StringComparison.OrdinalIgnoreCase))
            .Select(line => line["Sitemap:".Length..].Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<SofaScoreSitemapEntry> ParseSitemapEntries(byte[] payload, string sourceUrl)
    {
        if (payload.Length == 0)
        {
            return [];
        }

        var xml = ReadSitemapPayload(payload, sourceUrl);
        var doc = XDocument.Parse(xml);
        var namespaceName = doc.Root?.Name.Namespace ?? XNamespace.None;

        if (doc.Root?.Name.LocalName.Equals("sitemapindex", StringComparison.OrdinalIgnoreCase) == true)
        {
            return doc.Root.Elements(namespaceName + "sitemap")
                .Select(element => new SofaScoreSitemapEntry(
                    element.Element(namespaceName + "loc")?.Value?.Trim() ?? string.Empty,
                    ParseLastModified(element.Element(namespaceName + "lastmod")?.Value)))
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Location))
                .ToList();
        }

        if (doc.Root?.Name.LocalName.Equals("urlset", StringComparison.OrdinalIgnoreCase) == true)
        {
            return doc.Root.Elements(namespaceName + "url")
                .Select(element => new SofaScoreSitemapEntry(
                    element.Element(namespaceName + "loc")?.Value?.Trim() ?? string.Empty,
                    ParseLastModified(element.Element(namespaceName + "lastmod")?.Value)))
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Location))
                .ToList();
        }

        return [];
    }

    public static string BuildTeamSlug(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        var previousWasDash = false;

        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                previousWasDash = false;
                continue;
            }

            if ((char.IsWhiteSpace(ch) || ch is '-' or '_' or '/' or '&' or '.') && !previousWasDash)
            {
                builder.Append('-');
                previousWasDash = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    public static IReadOnlyList<string> BuildSlugCandidates(string teamName)
    {
        var slug = BuildTeamSlug(teamName);
        if (string.IsNullOrWhiteSpace(slug))
        {
            return [];
        }

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            slug
        };

        foreach (var suffix in new[] { "-fc", "-cf", "-sc", "-afc", "-fk", "-ac", "-cd", "-club", "-women", "-w", "-u21", "-u22", "-u23", "-ii", "-b" })
        {
            if (slug.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var trimmed = slug[..^suffix.Length].Trim('-');
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    candidates.Add(trimmed);
                }
            }
        }

        return candidates.OrderByDescending(candidate => candidate.Length).ToList();
    }

    public static bool UrlLooksRelevantForFixture(
        string eventUrl,
        IReadOnlyCollection<string> homeSlugCandidates,
        IReadOnlyCollection<string> awaySlugCandidates)
    {
        if (string.IsNullOrWhiteSpace(eventUrl))
        {
            return false;
        }

        var normalized = eventUrl.ToLowerInvariant();
        var homeMatches = homeSlugCandidates.Any(candidate => normalized.Contains(candidate, StringComparison.OrdinalIgnoreCase));
        var awayMatches = awaySlugCandidates.Any(candidate => normalized.Contains(candidate, StringComparison.OrdinalIgnoreCase));
        return homeMatches && awayMatches;
    }

    public static int ScoreUrlAgainstFixture(
        string eventUrl,
        IReadOnlyCollection<string> homeSlugCandidates,
        IReadOnlyCollection<string> awaySlugCandidates)
    {
        if (!UrlLooksRelevantForFixture(eventUrl, homeSlugCandidates, awaySlugCandidates))
        {
            return 0;
        }

        var score = 0;
        foreach (var candidate in homeSlugCandidates)
        {
            if (eventUrl.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                score += candidate.Length * 2;
                break;
            }
        }

        foreach (var candidate in awaySlugCandidates)
        {
            if (eventUrl.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                score += candidate.Length * 2;
                break;
            }
        }

        return score;
    }

    private static string ReadSitemapPayload(byte[] payload, string sourceUrl)
    {
        using var payloadStream = new MemoryStream(payload);
        Stream xmlStream = payloadStream;

        if (sourceUrl.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) || LooksLikeGZip(payload))
        {
            xmlStream = new GZipStream(payloadStream, CompressionMode.Decompress);
        }

        using var reader = new StreamReader(xmlStream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static bool LooksLikeGZip(byte[] payload)
    {
        return payload.Length >= 2 && payload[0] == 0x1F && payload[1] == 0x8B;
    }

    private static DateTime? ParseLastModified(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime
            : null;
    }
}

public sealed record SofaScoreSitemapEntry(string Location, DateTime? LastModifiedUtc);
