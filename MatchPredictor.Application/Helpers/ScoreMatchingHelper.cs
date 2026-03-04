using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using MatchPredictor.Domain.Helpers;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Application.Helpers;

public static class ScoreMatchingHelper
{
    private static readonly char[] Separators = { '-', '.', ':', ',', '/', '(', ')', ' ' };

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
        var wordsA = ExtractTeamWords(nameA);
        var wordsB = ExtractTeamWords(nameB);

        if (wordsA.Count == 0 || wordsB.Count == 0) return false;

        var shorter = wordsA.Count <= wordsB.Count ? wordsA : wordsB;
        var longer = wordsA.Count <= wordsB.Count ? wordsB : wordsA;

        var matchCount = 0;

        foreach (var shortWord in shorter)
        {
            // 1. Exact match (fastest)
            if (longer.Contains(shortWord))
            {
                matchCount++;
                continue;
            }

            // 2. Substring / prefix match for short words (≥3 chars)
            //    e.g., "lyon" matches "lyonnais", "glad" matches "gladbach"
            if (shortWord.Length >= 3)
            {
                bool prefixMatch = false;
                foreach (var longWord in longer)
                {
                    if (longWord.StartsWith(shortWord, StringComparison.OrdinalIgnoreCase) ||
                        shortWord.StartsWith(longWord, StringComparison.OrdinalIgnoreCase))
                    {
                        prefixMatch = true;
                        break;
                    }
                }
                if (prefixMatch)
                {
                    matchCount++;
                    continue;
                }
            }

            // 3. Levenshtein fuzzy match with dynamic tolerance
            //    - Less than 4 chars: 0 typos (exact only — already checked above)
            //    - 4 to 6 chars: 1 typo allowed
            //    - 7+ chars: 2 typos allowed
            int allowedTypos = shortWord.Length >= 7 ? 2 : (shortWord.Length >= 4 ? 1 : 0);

            if (allowedTypos > 0)
            {
                bool isFuzzyMatch = false;
                foreach (var longWord in longer)
                {
                    int distance = ComputeLevenshteinDistance(shortWord, longWord);
                    if (distance <= allowedTypos)
                    {
                        isFuzzyMatch = true;
                        break;
                    }
                }
                if (isFuzzyMatch) matchCount++;
            }
        }

        var ratio = (double)matchCount / shorter.Count;
        return ratio >= 0.5;
    }

    public static bool LeaguesMatch(string leagueA, string leagueB)
    {
        var wordsA = ExtractWords(leagueA, LeagueStopWords);
        var wordsB = ExtractWords(leagueB, LeagueStopWords);

        if (wordsA.Count == 0 || wordsB.Count == 0) return false;

        var shorter = wordsA.Count <= wordsB.Count ? wordsA : wordsB;
        var longer = wordsA.Count <= wordsB.Count ? wordsB : wordsA;

        var matchCount = shorter.Count(w => longer.Contains(w));
        return ((double)matchCount / shorter.Count) >= 0.4;
    }

    private static HashSet<string> ExtractTeamWords(string name)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        // Normalize diacritics first (São → Sao, Zürich → Zurich, etc.)
        var normalized = RemoveDiacritics(name);
        
        var parts = normalized.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            if (TeamStopWords.Contains(part)) continue;

            // Normalize abbreviations
            var finalWord = CommonSynonyms.TryGetValue(part, out var synonym) ? synonym : part;
            words.Add(finalWord);
        }

        return words;
    }

    private static HashSet<string> ExtractWords(string name, HashSet<string> stopWords)
    {
        var normalized = RemoveDiacritics(name);
        var parts = normalized.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        return parts.Where(p => !stopWords.Contains(p))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Strips diacritical marks so accented characters match their plain equivalents.
    /// e.g., "São Paulo" → "Sao Paulo", "Malmö FF" → "Malmo FF"
    /// </summary>
    private static string RemoveDiacritics(string text)
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

    public static string DetermineDrawOutcome(string score)
    {
        return ScoreParser.TryParse(score, out var h, out var a)
            ? (h == a ? "Draw" : "Not Draw") : "Unknown";
    }

    public static string DetermineOver25Outcome(string score)
    {
        return ScoreParser.TryParse(score, out var h, out var a)
            ? ((h + a) > 2 ? "Over 2.5" : "Under 2.5") : "Unknown";
    }

    public static string DetermineStraightWinOutcome(string score)
    {
        if (!ScoreParser.TryParse(score, out var h, out var a)) return "Unknown";
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
}