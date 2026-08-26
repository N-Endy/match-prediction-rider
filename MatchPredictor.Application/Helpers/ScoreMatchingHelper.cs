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

    public readonly record struct TeamMatchResult(
        bool IsMatch,
        double Score,
        bool IsExactKeyMatch,
        bool HasQualifierMismatch)
    {
        public static TeamMatchResult From(TeamIdentityMatcher.TeamMatchResult result) =>
            new(result.IsMatch, result.Score, result.IsExactKeyMatch, result.HasQualifierMismatch);
    }

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
                    "Under2.5Goals"  => DetermineOver25Outcome(match.Score),
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
                "Under2.5Goals"  => DetermineOver25Outcome(match.Score),
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
        return TeamMatchResult.From(TeamIdentityMatcher.GetTeamMatchResult(nameA, nameB));
    }

    public static TeamMatchResult GetTeamMatchResult(string nameA, string nameB, string? leagueA, string? leagueB)
    {
        return TeamMatchResult.From(TeamIdentityMatcher.GetTeamMatchResult(nameA, nameB, leagueA, leagueB));
    }

    /// <summary>
    /// Admin score-hint matching: same token scoring as settlement, but softens Reserve / U19–U21
    /// mismatches when the leagues suggest youth or reserve context. Does not change settlement.
    /// </summary>
    public static TeamMatchResult GetTeamMatchResultForAdminHint(
        string nameA,
        string nameB,
        string? leagueA,
        string? leagueB)
    {
        return TeamMatchResult.From(
            TeamIdentityMatcher.GetTeamMatchResultForAdminHint(nameA, nameB, leagueA, leagueB));
    }

    public static string CreateTeamLookupKey(string? name)
    {
        return CreateTeamLookupKey(name, null);
    }

    public static string CreateTeamLookupKey(string? name, string? league)
    {
        return TeamIdentityMatcher.CreateTeamLookupKey(name, league);
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
}
