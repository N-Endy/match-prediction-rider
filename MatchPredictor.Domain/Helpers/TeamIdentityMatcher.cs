using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MatchPredictor.Domain.Helpers;

/// <summary>
/// Qualifier-aware team identity matching shared by settlement, admin hints, and feature snapshots.
/// Strict mode rejects women/reserve/youth mismatches; hint mode softens reserve/U19–U21 for human review.
/// </summary>
public static class TeamIdentityMatcher
{
    private static readonly char[] Separators = { '-', '.', ':', ',', '/', '(', ')', ' ' };
    private static readonly Regex CountrySuffixRegex = new(@"\([A-Za-z]{3}\)", RegexOptions.Compiled);
    private static readonly Regex AgeQualifierRegex = new(@"\b(?:u|under)[\s-]?(\d{2})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> TeamStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "fc", "cf", "sc", "afc", "fcv", "fsv", "balompie", "esporte", "clube",
        "cd", "ud", "rcd", "fk", "sk", "nk", "bk", "if", "bsc", "tsv",
        "vfb", "vfl", "ssc", "as", "us", "og", "1fc", "ac", "rc", "se",
        "ssd", "srl", "sad", "sag", "spa",
        "de", "da", "do", "la", "le", "los", "del", "al", "el", "di", "il", "des", "den", "het"
    };

    private static readonly HashSet<string> WeakTeamTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "athletic", "atletico", "boys", "city", "deportivo", "dynamo",
        "international", "inter", "juniors", "old", "olympique",
        "real", "rovers", "saint", "sporting", "united", "wanderers"
    };

    private static readonly Dictionary<string, string> TeamQualifierSynonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        { "b", "reserve" },
        { "ii", "reserve" },
        { "iii", "reserve3" },
        { "res", "reserve" },
        { "reserve", "reserve" },
        { "reserves", "reserve" },
        { "w", "women" },
        { "women", "women" },
        { "ladies", "women" },
        { "fem", "women" },
        { "femenino", "women" },
        { "feminino", "women" },
        { "youth", "youth" },
        { "academy", "youth" }
    };

    private static readonly Dictionary<string, string> CommonSynonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        { "man", "manchester" },
        { "utd", "united" },
        { "st", "saint" },
        { "intl", "international" },
        { "sp", "sporting" },
        { "atl", "atletico" },
        { "cty", "city" },
        { "cit", "city" },
        { "weds", "wednesday" },
        { "ath", "athletic" },
        { "bor", "borough" },
        { "rov", "rovers" },
        { "int", "inter" },
        { "par", "partizan" },
        { "jun", "juniors" },
        { "snr", "seniors" },
        { "yth", "youth" },
        { "wand", "wanderers" },
        { "oly", "olympique" },
        { "dep", "deportivo" },
        { "dyn", "dynamo" },
        { "real", "real" },
        { "benf", "benfica" },
        { "ars", "arsenal" },
        { "bar", "barcelona" },
        { "tot", "tottenham" },
        { "chel", "chelsea" },
        { "liv", "liverpool" },
        { "new", "newcastle" },
        { "lei", "leicester" },
        { "wolv", "wolves" },
        { "sheff", "sheffield" },
        { "nott", "nottingham" },
        { "bri", "brighton" },
        { "kiev", "kyiv" },
        { "lvov", "lviv" },
        { "kharkov", "kharkiv" },
        { "odessa", "odesa" }
    };

    public readonly record struct TeamMatchResult(
        bool IsMatch,
        double Score,
        bool IsExactKeyMatch,
        bool HasQualifierMismatch);

    public static TeamMatchResult GetTeamMatchResult(string? nameA, string? nameB) =>
        GetTeamMatchResult(nameA, nameB, null, null);

    public static TeamMatchResult GetTeamMatchResult(
        string? nameA,
        string? nameB,
        string? leagueA,
        string? leagueB)
    {
        var teamA = ParseTeamIdentity(nameA, leagueA);
        var teamB = ParseTeamIdentity(nameB, leagueB);

        if (teamA.CoreTokens.Count == 0 || teamB.CoreTokens.Count == 0)
        {
            return new TeamMatchResult(false, 0, false, false);
        }

        if (!QualifiersMatch(teamA, teamB))
        {
            return new TeamMatchResult(false, 0, false, true);
        }

        return ComputeCoreTeamMatch(teamA, teamB);
    }

    /// <summary>
    /// Soft qualifier gate for admin near-miss score hints only.
    /// </summary>
    public static TeamMatchResult GetTeamMatchResultForAdminHint(
        string? nameA,
        string? nameB,
        string? leagueA,
        string? leagueB)
    {
        var teamA = ParseTeamIdentity(nameA, leagueA);
        var teamB = ParseTeamIdentity(nameB, leagueB);

        if (teamA.CoreTokens.Count == 0 || teamB.CoreTokens.Count == 0)
        {
            return new TeamMatchResult(false, 0, false, false);
        }

        if (!HintQualifiersCompatible(teamA, teamB, leagueA, leagueB))
        {
            return new TeamMatchResult(false, 0, false, true);
        }

        return ComputeCoreTeamMatch(teamA, teamB);
    }

    public static string CreateTeamLookupKey(string? name, string? league = null) =>
        ParseTeamIdentity(name, league).LookupKey;

    public static TeamIdentity ParseTeamIdentity(string? name, string? league)
    {
        var coreTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var qualifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var normalized = PreNormalizeTeamName(name);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return TeamIdentity.Empty;
        }

        var parts = normalized.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            var token = NormalizeToken(part);
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            if (TryNormalizeQualifier(token, out var qualifier))
            {
                qualifiers.Add(qualifier);
                continue;
            }

            if (CommonSynonyms.TryGetValue(token, out var synonym))
            {
                token = synonym;
            }

            if (TryNormalizeQualifier(token, out qualifier))
            {
                qualifiers.Add(qualifier);
                continue;
            }

            if (TeamStopWords.Contains(token))
            {
                continue;
            }

            coreTokens.Add(token);
        }

        foreach (var inferredQualifier in InferLeagueQualifiers(league))
        {
            qualifiers.Add(inferredQualifier);
        }

        if (coreTokens.Count == 0)
        {
            return TeamIdentity.Empty with
            {
                Qualifiers = qualifiers,
                LookupKey = BuildLookupKey(Array.Empty<string>(), qualifiers)
            };
        }

        var orderedCore = coreTokens
            .OrderBy(token => token, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var totalWeight = orderedCore.Sum(GetTokenWeight);
        var strongTokenCount = orderedCore.Count(token => !WeakTeamTokens.Contains(token));

        return new TeamIdentity
        {
            CoreTokens = orderedCore,
            CoreTokenSet = coreTokens,
            Qualifiers = qualifiers,
            TotalWeight = totalWeight,
            StrongTokenCount = strongTokenCount,
            LookupKey = BuildLookupKey(orderedCore, qualifiers)
        };
    }

    private static TeamMatchResult ComputeCoreTeamMatch(TeamIdentity teamA, TeamIdentity teamB)
    {
        if (string.Equals(teamA.LookupKey, teamB.LookupKey, StringComparison.Ordinal))
        {
            return new TeamMatchResult(true, 1.0, true, false);
        }

        var shorter = teamA.TotalWeight <= teamB.TotalWeight ? teamA : teamB;
        var longer = ReferenceEquals(shorter, teamA) ? teamB : teamA;

        var matchedWeight = 0.0;
        var matchedStrongTokens = 0;

        foreach (var token in shorter.CoreTokens)
        {
            var weight = GetTokenWeight(token);

            if (longer.CoreTokenSet.Contains(token))
            {
                matchedWeight += weight;
                if (!WeakTeamTokens.Contains(token))
                {
                    matchedStrongTokens++;
                }

                continue;
            }

            if (token.Length >= 4 && longer.CoreTokens.Any(longToken =>
                    longToken.StartsWith(token, StringComparison.OrdinalIgnoreCase) ||
                    token.StartsWith(longToken, StringComparison.OrdinalIgnoreCase)))
            {
                matchedWeight += weight * 0.92;
                if (!WeakTeamTokens.Contains(token))
                {
                    matchedStrongTokens++;
                }

                continue;
            }

            var allowedTypos = GetAllowedTypos(token);
            if (allowedTypos > 0 && longer.CoreTokens.Any(longToken =>
                    ComputeLevenshteinDistance(token, longToken) <= allowedTypos))
            {
                matchedWeight += weight * 0.82;
                if (!WeakTeamTokens.Contains(token))
                {
                    matchedStrongTokens++;
                }
            }
        }

        if (shorter.TotalWeight <= 0)
        {
            return new TeamMatchResult(false, 0, false, false);
        }

        var ratio = Math.Min(0.99, matchedWeight / shorter.TotalWeight);
        var strongRatio = shorter.StrongTokenCount == 0
            ? 1.0
            : matchedStrongTokens / (double)shorter.StrongTokenCount;

        var isMatch = ratio >= 0.74 && strongRatio >= 0.5;
        return new TeamMatchResult(isMatch, ratio, false, false);
    }

    private static string PreNormalizeTeamName(string? name)
    {
        var normalized = RemoveDiacritics(name);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        normalized = normalized.Replace('’', '\'');
        normalized = normalized.Replace("'", string.Empty, StringComparison.Ordinal);
        normalized = CountrySuffixRegex.Replace(normalized, " ");
        normalized = AgeQualifierRegex.Replace(normalized, "u$1");
        return normalized;
    }

    private static string NormalizeToken(string token) => token.Trim().ToLowerInvariant();

    private static string BuildLookupKey(IEnumerable<string> coreTokens, IEnumerable<string> qualifiers)
    {
        var coreKey = string.Join('|', coreTokens);
        var qualifierKey = string.Join('|', qualifiers.OrderBy(q => q, StringComparer.OrdinalIgnoreCase));
        return $"{coreKey}#{qualifierKey}".TrimEnd('#');
    }

    private static bool TryNormalizeQualifier(string token, out string qualifier)
    {
        qualifier = string.Empty;

        if (AgeQualifierRegex.IsMatch(token))
        {
            qualifier = AgeQualifierRegex.Replace(token, "u$1").ToLowerInvariant();
            return true;
        }

        if (TeamQualifierSynonyms.TryGetValue(token, out var normalizedQualifier))
        {
            qualifier = normalizedQualifier;
            return true;
        }

        return false;
    }

    private static IEnumerable<string> InferLeagueQualifiers(string? league)
    {
        if (string.IsNullOrWhiteSpace(league))
        {
            yield break;
        }

        var normalizedLeague = PreNormalizeTeamName(league);
        if (string.IsNullOrWhiteSpace(normalizedLeague))
        {
            yield break;
        }

        if (normalizedLeague.Contains("women", StringComparison.OrdinalIgnoreCase) ||
            normalizedLeague.Contains("ladies", StringComparison.OrdinalIgnoreCase) ||
            normalizedLeague.Contains("w-league", StringComparison.OrdinalIgnoreCase) ||
            normalizedLeague.Contains("w league", StringComparison.OrdinalIgnoreCase))
        {
            yield return "women";
        }

        if (normalizedLeague.Contains("youth", StringComparison.OrdinalIgnoreCase))
        {
            yield return "youth";
        }

        var ageQualifierMatch = AgeQualifierRegex.Match(normalizedLeague);
        if (ageQualifierMatch.Success)
        {
            yield return $"u{ageQualifierMatch.Groups[1].Value}";
        }
    }

    private static bool QualifiersMatch(TeamIdentity teamA, TeamIdentity teamB)
    {
        var womenA = teamA.Qualifiers.Contains("women");
        var womenB = teamB.Qualifiers.Contains("women");
        if (womenA != womenB)
        {
            return false;
        }

        var reserveA = teamA.Qualifiers.Contains("reserve") || teamA.Qualifiers.Contains("reserve3");
        var reserveB = teamB.Qualifiers.Contains("reserve") || teamB.Qualifiers.Contains("reserve3");
        if (reserveA != reserveB)
        {
            return false;
        }

        var ageA = teamA.Qualifiers.FirstOrDefault(q => q.StartsWith("u", StringComparison.OrdinalIgnoreCase));
        var ageB = teamB.Qualifiers.FirstOrDefault(q => q.StartsWith("u", StringComparison.OrdinalIgnoreCase));
        if (!string.Equals(ageA, ageB, StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrEmpty(ageA) && string.IsNullOrEmpty(ageB);
        }

        var youthA = teamA.Qualifiers.Contains("youth");
        var youthB = teamB.Qualifiers.Contains("youth");
        return youthA == youthB;
    }

    private static bool HintQualifiersCompatible(
        TeamIdentity teamA,
        TeamIdentity teamB,
        string? leagueA,
        string? leagueB)
    {
        var womenA = teamA.Qualifiers.Contains("women");
        var womenB = teamB.Qualifiers.Contains("women");
        if (womenA != womenB)
        {
            return false;
        }

        var reserveA = teamA.Qualifiers.Contains("reserve") || teamA.Qualifiers.Contains("reserve3");
        var reserveB = teamB.Qualifiers.Contains("reserve") || teamB.Qualifiers.Contains("reserve3");
        if (reserveA != reserveB &&
            !LeagueSuggestsReserve(leagueA) &&
            !LeagueSuggestsReserve(leagueB))
        {
            return false;
        }

        var ageA = teamA.Qualifiers.FirstOrDefault(q => q.StartsWith("u", StringComparison.OrdinalIgnoreCase));
        var ageB = teamB.Qualifiers.FirstOrDefault(q => q.StartsWith("u", StringComparison.OrdinalIgnoreCase));
        if (!AgesHintCompatible(ageA, ageB, leagueA, leagueB))
        {
            return false;
        }

        var youthA = teamA.Qualifiers.Contains("youth") || IsSoftYouthAge(ageA) || LeagueSuggestsYouth(leagueA);
        var youthB = teamB.Qualifiers.Contains("youth") || IsSoftYouthAge(ageB) || LeagueSuggestsYouth(leagueB);
        if (youthA != youthB &&
            !LeagueSuggestsYouth(leagueA) &&
            !LeagueSuggestsYouth(leagueB))
        {
            return false;
        }

        return true;
    }

    private static bool AgesHintCompatible(string? ageA, string? ageB, string? leagueA, string? leagueB)
    {
        if (string.Equals(ageA, ageB, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(ageA) && !string.IsNullOrEmpty(ageB))
        {
            return IsSoftYouthAge(ageA) && IsSoftYouthAge(ageB);
        }

        if (string.IsNullOrEmpty(ageA) && IsSoftYouthAge(ageB))
        {
            return LeagueSuggestsYouth(leagueA) || LeagueSuggestsYouth(leagueB);
        }

        if (string.IsNullOrEmpty(ageB) && IsSoftYouthAge(ageA))
        {
            return LeagueSuggestsYouth(leagueA) || LeagueSuggestsYouth(leagueB);
        }

        return string.IsNullOrEmpty(ageA) && string.IsNullOrEmpty(ageB);
    }

    private static bool IsSoftYouthAge(string? age) =>
        string.Equals(age, "u19", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(age, "u20", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(age, "u21", StringComparison.OrdinalIgnoreCase);

    private static bool LeagueSuggestsReserve(string? league)
    {
        var normalized = PreNormalizeTeamName(league);
        return !string.IsNullOrWhiteSpace(normalized) &&
               normalized.Contains("reserve", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LeagueSuggestsYouth(string? league)
    {
        var normalized = PreNormalizeTeamName(league);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return normalized.Contains("youth", StringComparison.OrdinalIgnoreCase) ||
               AgeQualifierRegex.IsMatch(normalized);
    }

    private static double GetTokenWeight(string token) =>
        WeakTeamTokens.Contains(token) ? 0.35 : 1.0;

    private static int GetAllowedTypos(string token) =>
        token.Length switch
        {
            >= 8 => 2,
            >= 5 => 1,
            _ => 0
        };

    private static string? RemoveDiacritics(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);

        foreach (var c in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static int ComputeLevenshteinDistance(string source, string target)
    {
        source = source.ToLowerInvariant();
        target = target.ToLowerInvariant();

        if (source.Length == 0)
        {
            return target.Length;
        }

        if (target.Length == 0)
        {
            return source.Length;
        }

        var v0 = new int[target.Length + 1];
        var v1 = new int[target.Length + 1];

        for (var i = 0; i < v0.Length; i++)
        {
            v0[i] = i;
        }

        for (var i = 0; i < source.Length; i++)
        {
            v1[0] = i + 1;
            for (var j = 0; j < target.Length; j++)
            {
                var cost = source[i] == target[j] ? 0 : 1;
                v1[j + 1] = Math.Min(Math.Min(v1[j] + 1, v0[j + 1] + 1), v0[j] + cost);
            }

            for (var j = 0; j < v0.Length; j++)
            {
                v0[j] = v1[j];
            }
        }

        return v1[target.Length];
    }

    public sealed record TeamIdentity
    {
        public static TeamIdentity Empty { get; } = new()
        {
            CoreTokens = Array.Empty<string>(),
            CoreTokenSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Qualifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            LookupKey = string.Empty,
            TotalWeight = 0,
            StrongTokenCount = 0
        };

        public IReadOnlyList<string> CoreTokens { get; init; } = Array.Empty<string>();
        public HashSet<string> CoreTokenSet { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Qualifiers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public string LookupKey { get; init; } = string.Empty;
        public double TotalWeight { get; init; }
        public int StrongTokenCount { get; init; }
    }
}
