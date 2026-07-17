using System.Text.RegularExpressions;

namespace MatchPredictor.Domain.Helpers;

public static partial class TeamNameNormalizer
{
    /// <summary>
    /// Max length for values stored in PostgreSQL btree unique indexes on Teams/TeamAliases.
    /// Keeps index rows under the ~2704-byte btree limit even with multi-byte UTF-8 characters.
    /// </summary>
    public const int MaxIndexedValueLength = 256;

    /// <summary>
    /// Reject clearly garbage scrape payloads before they become alias candidates.
    /// </summary>
    public const int MaxRawTeamNameLength = 200;

    public const int MaxRawLeagueLength = 300;

    private static readonly Dictionary<string, string> TokenSynonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["man"] = "manchester",
        ["psg"] = "paris saint germain",
        ["utd"] = "united",
        ["st"] = "saint",
        ["intl"] = "international",
        ["sp"] = "sporting",
        ["atl"] = "atletico",
        ["w"] = "women",
        ["ladies"] = "women",
        ["fem"] = "women",
        ["femenino"] = "women",
        ["feminino"] = "women",
        ["ii"] = "reserve",
        ["b"] = "reserve",
        ["res"] = "reserve",
        ["reserves"] = "reserve"
    };

    public static string NormalizeAlias(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = NonWordRegex().Replace(value.ToLowerInvariant(), " ");
        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => TokenSynonyms.TryGetValue(token, out var replacement) ? replacement : token)
            .ToArray();

        return BoundIndexedValue(string.Join(' ', tokens).Trim());
    }

    public static string NormalizeLeagueScope(string? league)
    {
        return NormalizeAlias(league);
    }

    public static string BoundIndexedValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= MaxIndexedValueLength
            ? value
            : value[..MaxIndexedValueLength];
    }

    public static bool IsUsableAliasCandidate(string? teamName, string? league)
    {
        if (string.IsNullOrWhiteSpace(teamName) || teamName.Length > MaxRawTeamNameLength)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(league) && league.Length > MaxRawLeagueLength)
        {
            return false;
        }

        // Scraped HTML/JSON blobs occasionally leak into team/league fields.
        if (teamName.Contains('<', StringComparison.Ordinal) ||
            teamName.Contains('{', StringComparison.Ordinal) ||
            (!string.IsNullOrWhiteSpace(league) &&
             (league.Contains('<', StringComparison.Ordinal) || league.Contains('{', StringComparison.Ordinal))))
        {
            return false;
        }

        return true;
    }

    public static string BuildAliasKey(string? alias, string? leagueScope)
    {
        var normalizedAlias = NormalizeAlias(alias);
        if (string.IsNullOrWhiteSpace(normalizedAlias))
        {
            return string.Empty;
        }

        var normalizedLeague = NormalizeLeagueScope(leagueScope);
        return string.IsNullOrWhiteSpace(normalizedLeague)
            ? normalizedAlias
            : $"{normalizedLeague}|{normalizedAlias}";
    }

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.Compiled)]
    private static partial Regex NonWordRegex();
}
