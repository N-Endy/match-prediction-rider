using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Application.Helpers;

public static class ScoreMatchingHelper
{
    private static readonly char[] Separators = { '-', '.', ':', ',', '/', '(', ')', ' ' };
    private static readonly Regex CountrySuffixRegex = new(@"\([A-Za-z]{3}\)", RegexOptions.Compiled);
    private static readonly Regex AgeQualifierRegex = new(@"\b(?:u|under)[\s-]?(\d{2})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Common league noise words
    private static readonly HashSet<string> LeagueStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "standings", "qualification", "play", "offs", "round",
        "group", "stage", "phase", "preliminary", "league", "championship"
    };

    // Generic team suffixes/noise words that differ across sources
    private static readonly HashSet<string> TeamStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Club type suffixes
        "fc", "cf", "sc", "afc", "fcv", "fsv", "balompie", "esporte", "clube",
        "cd", "ud", "rcd", "fk", "sk", "nk", "bk", "if", "bsc", "tsv",
        "vfb", "vfl", "ssc", "as", "us", "og", "1fc", "ac", "rc", "se",
        "ssd", "srl", "sad", "sag", "spa",
        // Prepositions / articles common in team names
        "de", "da", "do", "la", "le", "los", "del", "al", "el", "di", "il", "des", "den", "het"
    };

    // Common football terms that are too generic to identify a club on their own.
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

    // Map common abbreviations to their full words
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
        { "bri", "brighton" }
    };

    public readonly record struct TeamMatchResult(
        bool IsMatch,
        double Score,
        bool IsExactKeyMatch,
        bool HasQualifierMismatch);

    public static void PatchMissingScores(List<Prediction> predictions, List<MatchScore> scores)
    {
        if (scores.Count == 0 || predictions.Count == 0) return;

        foreach (var prediction in predictions)
        {
            var match = scores.FirstOrDefault(s =>
                TeamsMatch(s.HomeTeam, prediction.HomeTeam) &&
                TeamsMatch(s.AwayTeam, prediction.AwayTeam));

            if (match == null) continue;

            if (string.IsNullOrEmpty(prediction.ActualScore) || match.IsLive)
            {
                prediction.ActualScore = match.Score;
                prediction.IsLive = match.IsLive;
                prediction.ActualOutcome = prediction.PredictionCategory switch
                {
                    "BothTeamsScore" => match.BTTSLabel ? "BTTS" : "No BTTS",
                    "Draw"           => DetermineDrawOutcome(match.Score),
                    "Over2.5Goals"   => DetermineOver25Outcome(match.Score),
                    "StraightWin"    => DetermineStraightWinOutcome(match.Score),
                    _                => null
                };
            }
            else if (!match.IsLive)
            {
                prediction.IsLive = false;
            }
        }
    }

    /// <summary>
    /// Fallback: patches predictions that still have no score using AiScore data.
    /// </summary>
    public static void PatchMissingScores(List<Prediction> predictions, List<AiScoreMatchScore> aiScores)
    {
        if (aiScores.Count == 0 || predictions.Count == 0) return;

        foreach (var prediction in predictions)
        {
            // Only patch if still missing a score
            if (!string.IsNullOrEmpty(prediction.ActualScore)) continue;

            var match = aiScores.FirstOrDefault(s =>
                TeamsMatch(s.HomeTeam, prediction.HomeTeam) &&
                TeamsMatch(s.AwayTeam, prediction.AwayTeam));

            if (match == null) continue;

            prediction.ActualScore = match.Score;
            prediction.IsLive = match.IsLive;
            prediction.ActualOutcome = prediction.PredictionCategory switch
            {
                "BothTeamsScore" => match.BTTSLabel ? "BTTS" : "No BTTS",
                "Draw"           => DetermineDrawOutcome(match.Score),
                "Over2.5Goals"   => DetermineOver25Outcome(match.Score),
                "StraightWin"    => DetermineStraightWinOutcome(match.Score),
                _                => null
            };
        }
    }

    public static bool TeamsMatch(string nameA, string nameB)
    {
        return GetTeamMatchResult(nameA, nameB).IsMatch;
    }

    public static bool LeaguesMatch(string leagueA, string leagueB)
    {
        return GetLeagueMatchScore(leagueA, leagueB) >= 0.6;
    }

    public static TeamMatchResult GetTeamMatchResult(string nameA, string nameB)
    {
        return GetTeamMatchResult(nameA, nameB, null, null);
    }

    public static TeamMatchResult GetTeamMatchResult(string nameA, string nameB, string? leagueA, string? leagueB)
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
                if (!WeakTeamTokens.Contains(token)) matchedStrongTokens++;
                continue;
            }

            if (token.Length >= 4 && longer.CoreTokens.Any(longToken =>
                    longToken.StartsWith(token, StringComparison.OrdinalIgnoreCase) ||
                    token.StartsWith(longToken, StringComparison.OrdinalIgnoreCase)))
            {
                matchedWeight += weight * 0.92;
                if (!WeakTeamTokens.Contains(token)) matchedStrongTokens++;
                continue;
            }

            var allowedTypos = GetAllowedTypos(token);
            if (allowedTypos > 0 && longer.CoreTokens.Any(longToken =>
                    ComputeLevenshteinDistance(token, longToken) <= allowedTypos))
            {
                matchedWeight += weight * 0.82;
                if (!WeakTeamTokens.Contains(token)) matchedStrongTokens++;
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

    public static string CreateTeamLookupKey(string? name)
    {
        return CreateTeamLookupKey(name, null);
    }

    public static string CreateTeamLookupKey(string? name, string? league)
    {
        return ParseTeamIdentity(name, league).LookupKey;
    }

    public static string CreateLeagueLookupKey(string? league)
    {
        var words = ExtractWords(league, LeagueStopWords)
            .OrderBy(word => word, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return string.Join('|', words).ToLowerInvariant();
    }

    public static double GetLeagueMatchScore(string? leagueA, string? leagueB)
    {
        var wordsA = ExtractWords(leagueA, LeagueStopWords);
        var wordsB = ExtractWords(leagueB, LeagueStopWords);

        if (wordsA.Count == 0 || wordsB.Count == 0) return 0;

        var keyA = string.Join('|', wordsA.OrderBy(word => word, StringComparer.OrdinalIgnoreCase));
        var keyB = string.Join('|', wordsB.OrderBy(word => word, StringComparer.OrdinalIgnoreCase));

        if (string.Equals(keyA, keyB, StringComparison.OrdinalIgnoreCase))
        {
            return 1.0;
        }

        var shorter = wordsA.Count <= wordsB.Count ? wordsA : wordsB;
        var longer = wordsA.Count <= wordsB.Count ? wordsB : wordsA;

        var matched = 0.0;

        foreach (var token in shorter)
        {
            if (longer.Contains(token))
            {
                matched += 1.0;
                continue;
            }

            if (token.Length >= 4 && longer.Any(longToken =>
                    longToken.StartsWith(token, StringComparison.OrdinalIgnoreCase) ||
                    token.StartsWith(longToken, StringComparison.OrdinalIgnoreCase)))
            {
                matched += 0.85;
            }
        }

        return matched / shorter.Count;
    }

    private static TeamIdentity ParseTeamIdentity(string? name, string? league)
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
            if (string.IsNullOrWhiteSpace(token)) continue;

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

            if (TeamStopWords.Contains(token)) continue;
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

    private static HashSet<string> ExtractWords(string? name, HashSet<string> stopWords)
    {
        var normalized = RemoveDiacritics(name);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var parts = normalized.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        return parts.Where(p => !stopWords.Contains(p))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
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

    private static string NormalizeToken(string token)
    {
        return token.Trim().ToLowerInvariant();
    }

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
        if (womenA != womenB) return false;

        var reserveA = teamA.Qualifiers.Contains("reserve") || teamA.Qualifiers.Contains("reserve3");
        var reserveB = teamB.Qualifiers.Contains("reserve") || teamB.Qualifiers.Contains("reserve3");
        if (reserveA != reserveB) return false;

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

    private static double GetTokenWeight(string token)
    {
        return WeakTeamTokens.Contains(token) ? 0.35 : 1.0;
    }

    private static int GetAllowedTypos(string token)
    {
        return token.Length switch
        {
            >= 8 => 2,
            >= 5 => 1,
            _ => 0
        };
    }

    /// <summary>
    /// Strips diacritical marks so accented characters match their plain equivalents.
    /// e.g., "São Paulo" → "Sao Paulo", "Malmö FF" → "Malmo FF"
    /// </summary>
    private static string? RemoveDiacritics(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        
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

    // Combined parsing logic to avoid duplicating string splits
    private static (int Home, int Away, bool IsValid) ParseScore(string score)
    {
        var parts = score.Split(':');
        if (parts.Length == 2 && 
            int.TryParse(parts[0], out var h) && 
            int.TryParse(parts[1], out var a))
        {
            return (h, a, true);
        }
        return (0, 0, false);
    }

    private static string DetermineDrawOutcome(string score)
    {
        var (h, a, isValid) = ParseScore(score);
        return isValid ? (h == a ? "Draw" : "Not Draw") : "Unknown";
    }

    private static string DetermineOver25Outcome(string score)
    {
        var (h, a, isValid) = ParseScore(score);
        return isValid ? ((h + a) > 2 ? "Over 2.5" : "Under 2.5") : "Unknown";
    }

    private static string DetermineStraightWinOutcome(string score)
    {
        var (h, a, isValid) = ParseScore(score);
        if (!isValid) return "Unknown";
        
        if (h > a) return "Home Win";
        return h < a ? "Away Win" : "Draw";
    }
    
    /// <summary>
    /// Calculates the minimum number of character edits required to change one string into another.
    /// Case-insensitive comparison. Uses optimized memory footprint (two 1D arrays).
    /// </summary>
    private static int ComputeLevenshteinDistance(string source, string target)
    {
        // Lowercase both for case-insensitive comparison
        source = source.ToLowerInvariant();
        target = target.ToLowerInvariant();
        
        if (source.Length == 0) return target.Length;
        if (target.Length == 0) return source.Length;

        var v0 = new int[target.Length + 1];
        var v1 = new int[target.Length + 1];

        for (int i = 0; i < v0.Length; i++) v0[i] = i;

        for (int i = 0; i < source.Length; i++)
        {
            v1[0] = i + 1;
            for (int j = 0; j < target.Length; j++)
            {
                var cost = source[i] == target[j] ? 0 : 1;
                v1[j + 1] = Math.Min(Math.Min(v1[j] + 1, v0[j + 1] + 1), v0[j] + cost);
            }
            for (int j = 0; j < v0.Length; j++) v0[j] = v1[j];
        }
        return v1[target.Length];
    }

    private sealed record TeamIdentity
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
