using System;
using System.Collections.Generic;
using System.Linq;
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

    // Generic team suffixes that artificially inflate match ratios
    private static readonly HashSet<string> TeamStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "fc", "cf", "sc", "afc", "fcv", "fsv", "balompie", "esporte", "clube"
    };

    // Map common abbreviations to their full words so they match perfectly
    private static readonly Dictionary<string, string> CommonSynonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        { "man", "manchester" },
        { "utd", "united" },
        { "st", "saint" },
        { "intl", "international" },
        { "sp", "sporting" },
        { "atl", "atletico" }
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
            // 1. Check for an exact match first (Fastest)
            if (longer.Contains(shortWord))
            {
                matchCount++;
                continue;
            }

            // 2. Fallback to Levenshtein Fuzzy Match
            bool isFuzzyMatch = false;
        
            // Dynamic typo tolerance based on word length
            // - Less than 5 chars: 0 typos allowed (exact match only)
            // - 5 to 7 chars: 1 typo allowed
            // - 8+ chars: 2 typos allowed
            int allowedTypos = shortWord.Length >= 8 ? 2 : (shortWord.Length >= 5 ? 1 : 0);

            if (allowedTypos > 0)
            {
                foreach (var longWord in longer)
                {
                    int distance = ComputeLevenshteinDistance(shortWord, longWord);
                    if (distance <= allowedTypos)
                    {
                        isFuzzyMatch = true;
                        break;
                    }
                }
            }

            if (isFuzzyMatch) matchCount++;
        }

        var ratio = (double)matchCount / shorter.Count;
        return ratio >= 0.6;
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
        
        // string.Split is much faster than Regex here
        var parts = name.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            if (TeamStopWords.Contains(part)) continue;

            // Normalize abbreviations instantly
            var finalWord = CommonSynonyms.TryGetValue(part, out var synonym) ? synonym : part;
            words.Add(finalWord);
        }

        return words;
    }

    private static HashSet<string> ExtractWords(string name, HashSet<string> stopWords)
    {
        var parts = name.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        return parts.Where(p => !stopWords.Contains(p))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
    /// Uses an optimized memory footprint (two 1D arrays instead of a full 2D matrix).
    /// </summary>
    private static int ComputeLevenshteinDistance(string source, string target)
    {
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