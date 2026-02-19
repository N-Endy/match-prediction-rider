using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Application.Helpers;

public static class ScoreMatchingHelper
{
    // Noise words that appear in league names but don't help matching
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "standings", "qualification", "play", "offs", "round",
        "group", "stage", "phase", "preliminary"
    };

    /// <summary>
    /// Patches missing ActualScore/ActualOutcome on predictions by matching
    /// them to scraped MatchScores using word-overlap on team names.
    /// Call this at display-time so scores appear even if the background
    /// job hasn't re-run yet.
    /// </summary>
    public static void PatchMissingScores(
        List<Prediction> predictions,
        List<MatchScore> scores)
    {
        if (scores.Count == 0) return;

        foreach (var prediction in predictions.Where(p => string.IsNullOrEmpty(p.ActualScore)))
        {
            var match = scores.FirstOrDefault(s =>
                TeamsMatch(s.HomeTeam, prediction.HomeTeam) &&
                TeamsMatch(s.AwayTeam, prediction.AwayTeam));

            if (match == null) continue;

            prediction.ActualScore = match.Score;
            prediction.ActualOutcome = prediction.PredictionCategory switch
            {
                "BothTeamsScore" => match.BTTSLabel ? "BTTS" : "No BTTS",
                "Draw" => DetermineDrawOutcome(match.Score),
                "Over2.5Goals" => DetermineOver25Outcome(match.Score),
                "StraightWin" => DetermineStraightWinOutcome(match.Score),
                _ => null
            };
        }
    }

    /// <summary>
    /// Checks if two team names refer to the same team using word-overlap.
    /// Returns true if the shorter name's significant words are mostly found
    /// in the longer name. E.g. "Al-Nassr" ↔ "Al Nassr" → both have {al, nassr} → match.
    /// </summary>
    public static bool TeamsMatch(string nameA, string nameB)
    {
        var wordsA = ExtractWords(nameA);
        var wordsB = ExtractWords(nameB);

        if (wordsA.Count == 0 || wordsB.Count == 0)
            return false;

        // The shorter set's words should mostly appear in the longer set
        var shorter = wordsA.Count <= wordsB.Count ? wordsA : wordsB;
        var longer = wordsA.Count <= wordsB.Count ? wordsB : wordsA;

        var matchCount = shorter.Count(w => longer.Contains(w));
        var ratio = (double)matchCount / shorter.Count;

        // Require at least 70% word overlap (allows for minor differences like "FC", "CF")
        return ratio >= 0.7;
    }

    /// <summary>
    /// Checks if two league names refer to the same league using word-overlap,
    /// ignoring common noise words like "Standings", "Qualification", etc.
    /// </summary>
    public static bool LeaguesMatch(string leagueA, string leagueB)
    {
        var wordsA = ExtractWords(leagueA).Except(StopWords).ToHashSet();
        var wordsB = ExtractWords(leagueB).Except(StopWords).ToHashSet();

        if (wordsA.Count == 0 || wordsB.Count == 0)
            return false;

        var shorter = wordsA.Count <= wordsB.Count ? wordsA : wordsB;
        var longer = wordsA.Count <= wordsB.Count ? wordsB : wordsA;

        var matchCount = shorter.Count(w => longer.Contains(w));
        var ratio = (double)matchCount / shorter.Count;

        return ratio >= 0.6;
    }

    /// <summary>
    /// Extracts significant words from a name.
    /// "Al-Nassr" → {"al", "nassr"}
    /// "ASIA: AFC Champions League 2" → {"asia", "afc", "champions", "league", "2"}
    /// </summary>
    private static HashSet<string> ExtractWords(string name)
    {
        // Replace separators (hyphens, dots, colons, commas) with spaces, then split
        var cleaned = Regex.Replace(name.Trim().ToLowerInvariant(), @"[-.:,/()]+", " ");
        var words = Regex.Split(cleaned, @"\s+")
            .Where(w => w.Length > 0)
            .ToHashSet();
        return words;
    }

    private static string DetermineDrawOutcome(string score)
    {
        var parts = score.Split(':');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var h) &&
            int.TryParse(parts[1], out var a))
        {
            return h == a ? "Draw" : "Not Draw";
        }
        return "Unknown";
    }

    private static string DetermineOver25Outcome(string score)
    {
        var parts = score.Split(':');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var h) &&
            int.TryParse(parts[1], out var a))
        {
            return (h + a) > 2 ? "Over 2.5" : "Under 2.5";
        }
        return "Unknown";
    }

    private static string DetermineStraightWinOutcome(string score)
    {
        var parts = score.Split(':');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var h) &&
            int.TryParse(parts[1], out var a))
        {
            if (h > a) return "Home Win";
            return h < a ? "Away Win" : "Draw";
        }
        return "Unknown";
    }
}
