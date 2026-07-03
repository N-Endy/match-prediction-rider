using System.Text.RegularExpressions;

namespace MatchPredictor.Domain.Helpers;

public static partial class TeamNameNormalizer
{
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

        return string.Join(' ', tokens).Trim();
    }

    public static string NormalizeLeagueScope(string? league)
    {
        return NormalizeAlias(league);
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
