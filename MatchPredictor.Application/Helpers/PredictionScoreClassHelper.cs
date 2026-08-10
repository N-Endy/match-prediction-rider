using System.Text.RegularExpressions;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Application.Helpers;

/// <summary>
/// Shared score badge class rules (green only when the prediction matches the score).
/// </summary>
public static partial class PredictionScoreClassHelper
{
    public static string GetScoreClass(Prediction prediction, DateTime utcNow)
    {
        var isActuallyLive = IsActuallyLive(prediction, utcNow);

        if (isActuallyLive && string.Equals(prediction.PredictionCategory, "Under2.5Goals", StringComparison.OrdinalIgnoreCase))
        {
            if (TryParseScore(prediction.ActualScore, out var homeGoals, out var awayGoals) && homeGoals + awayGoals > 2)
            {
                return "mp-score-incorrect";
            }

            return "mp-score-live";
        }

        if (isActuallyLive)
        {
            return IsLivePredictionCorrect(prediction)
                ? "mp-score-correct"
                : "mp-score-live";
        }

        return IsPredictionCorrect(prediction)
            ? "mp-score-correct"
            : "mp-score-incorrect";
    }

    public static bool IsActuallyLive(Prediction prediction, DateTime utcNow)
    {
        if (!prediction.IsLive)
        {
            return false;
        }

        return !prediction.MatchDateTime.HasValue || utcNow <= prediction.MatchDateTime.Value.AddMinutes(200);
    }

    public static bool IsPredictionCorrect(Prediction prediction)
    {
        if (TryParseScore(prediction.ActualScore, out var homeGoals, out var awayGoals))
        {
            return prediction.PredictionCategory switch
            {
                "BothTeamsScore" => DoesBttsPredictionMatch(prediction.PredictedOutcome, homeGoals, awayGoals),
                "Over2.5Goals" => DoesOverPredictionMatch(prediction.PredictedOutcome, homeGoals, awayGoals),
                "Under2.5Goals" => DoesOverPredictionMatch(prediction.PredictedOutcome, homeGoals, awayGoals),
                "Draw" => DoesDrawPredictionMatch(prediction.PredictedOutcome, homeGoals, awayGoals),
                "StraightWin" => DoesStraightWinPredictionMatch(prediction.PredictedOutcome, homeGoals, awayGoals),
                _ => OutcomesMatch(prediction.PredictedOutcome, prediction.ActualOutcome)
            };
        }

        return OutcomesMatch(prediction.PredictedOutcome, prediction.ActualOutcome);
    }

    public static bool IsLivePredictionCorrect(Prediction prediction)
    {
        if (!prediction.IsLive)
        {
            return IsPredictionCorrect(prediction);
        }

        if (!TryParseScore(prediction.ActualScore, out var homeGoals, out var awayGoals))
        {
            return false;
        }

        return prediction.PredictionCategory switch
        {
            "BothTeamsScore" => DoesBttsPredictionMatch(prediction.PredictedOutcome, homeGoals, awayGoals),
            "Over2.5Goals" => DoesOverPredictionMatch(prediction.PredictedOutcome, homeGoals, awayGoals),
            "Under2.5Goals" => DoesOverPredictionMatch(prediction.PredictedOutcome, homeGoals, awayGoals),
            _ => false
        };
    }

    private static bool TryParseScore(string? score, out int homeGoals, out int awayGoals)
    {
        homeGoals = 0;
        awayGoals = 0;

        if (string.IsNullOrWhiteSpace(score))
        {
            return false;
        }

        var scoreParts = ScoreRegex()
            .Matches(score)
            .Select(match => match.Value)
            .ToArray();

        return scoreParts.Length >= 2
            && int.TryParse(scoreParts[0], out homeGoals)
            && int.TryParse(scoreParts[1], out awayGoals);
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex ScoreRegex();

    private static bool DoesBttsPredictionMatch(string? predictedOutcome, int homeGoals, int awayGoals)
    {
        var bothTeamsScored = homeGoals > 0 && awayGoals > 0;

        return NormalizeOutcome(predictedOutcome) switch
        {
            "btts" or "yes" or "gg" => bothTeamsScored,
            "no btts" or "no" or "ng" => !bothTeamsScored,
            _ => bothTeamsScored
        };
    }

    private static bool DoesOverPredictionMatch(string? predictedOutcome, int homeGoals, int awayGoals)
    {
        var isOver = homeGoals + awayGoals > 2;

        return NormalizeOutcome(predictedOutcome) switch
        {
            "over" or "over 2.5" or "over2.5" => isOver,
            "under" or "under 2.5" or "under2.5" => !isOver,
            _ => isOver
        };
    }

    private static bool DoesDrawPredictionMatch(string? predictedOutcome, int homeGoals, int awayGoals)
    {
        var isDraw = homeGoals == awayGoals;

        return NormalizeOutcome(predictedOutcome) switch
        {
            "draw" => isDraw,
            "not draw" => !isDraw,
            _ => isDraw
        };
    }

    private static bool DoesStraightWinPredictionMatch(string? predictedOutcome, int homeGoals, int awayGoals)
    {
        return NormalizeOutcome(predictedOutcome) switch
        {
            "home win" or "home" or "1" => homeGoals > awayGoals,
            "away win" or "away" or "2" => awayGoals > homeGoals,
            "draw" or "x" => homeGoals == awayGoals,
            _ => false
        };
    }

    private static bool OutcomesMatch(string? predictedOutcome, string? actualOutcome)
    {
        var normalizedPredicted = NormalizeOutcome(predictedOutcome);
        var normalizedActual = NormalizeOutcome(actualOutcome);

        return normalizedPredicted.Length > 0 &&
               normalizedActual.Length > 0 &&
               normalizedPredicted == normalizedActual;
    }

    private static string NormalizeOutcome(string? outcome)
    {
        if (string.IsNullOrWhiteSpace(outcome))
        {
            return string.Empty;
        }

        return Regex.Replace(outcome.Trim().ToLowerInvariant(), @"\s+", " ");
    }
}
