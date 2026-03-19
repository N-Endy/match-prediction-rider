using System.Globalization;
using System.Text.RegularExpressions;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Utils;

namespace MatchPredictor.Web.Helpers;

public static partial class PredictionDisplayHelper
{
    public static decimal GetConfidenceValue(Prediction prediction)
    {
        return prediction.ConfidenceScore ?? prediction.RawConfidenceScore ?? decimal.Zero;
    }

    public static string FormatWatTime(Prediction prediction)
    {
        if (prediction.MatchLocalTime.HasValue)
        {
            return DateTime.Today.Add(prediction.MatchLocalTime.Value.ToTimeSpan()).ToString("h:mm tt", CultureInfo.InvariantCulture);
        }

        if (DateTime.TryParse(
                prediction.Time,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedTime))
        {
            return parsedTime.ToString("h:mm tt");
        }

        if (prediction.MatchDateTime.HasValue)
        {
            return DateTimeProvider.ConvertUtcToLocal(prediction.MatchDateTime.Value).ToString("h:mm tt");
        }

        return prediction.Time;
    }

    public static string FormatLastUpdated(DateTime? localTime)
    {
        return localTime.HasValue
            ? $"{localTime.Value:dd MMM, h:mm tt} WAT"
            : "Awaiting first refresh";
    }

    public static string FormatRunReason(string? runReason)
    {
        if (string.IsNullOrWhiteSpace(runReason))
        {
            return "Live snapshot";
        }

        return NormalizeOutcome(runReason) switch
        {
            "prediction-generation" => "Daily generation",
            "prediction-prewarm" => "Pre-midnight prewarm",
            "initial" => "Initial sweep",
            "refresh" => "Refresh run",
            "prediction-generation-refresh" => "Refresh run",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(runReason.Replace('-', ' ').Trim())
        };
    }

    public static string GetPredictionBadgeClass(Prediction prediction)
    {
        return prediction.PredictionCategory switch
        {
            "BothTeamsScore" => "mp-badge-btts",
            "Over2.5Goals" => "mp-badge-over",
            "Under2.5Goals" => "mp-badge-under",
            "Draw" => "mp-badge-draw",
            "StraightWin" when string.Equals(prediction.PredictedOutcome, "Away Win", StringComparison.OrdinalIgnoreCase) => "mp-badge-win-away",
            "StraightWin" => "mp-badge-win-home",
            _ => "mp-badge-combined"
        };
    }

    public static string GetPredictionBadgeText(Prediction prediction)
    {
        return prediction.PredictionCategory switch
        {
            "BothTeamsScore" => "BTTS",
            "Over2.5Goals" or "Under2.5Goals" => prediction.PredictedOutcome ?? prediction.PredictionCategory,
            "Draw" => "Draw",
            "StraightWin" => prediction.PredictedOutcome ?? "Win",
            _ => prediction.PredictedOutcome ?? prediction.PredictionCategory
        };
    }

    public static string GetCartMarket(Prediction prediction)
    {
        return prediction.PredictionCategory switch
        {
            "BothTeamsScore" => "BTTS",
            "Over2.5Goals" => "Over2.5",
            "Under2.5Goals" => "Under2.5",
            "Draw" => "1X2",
            "StraightWin" => "StraightWin",
            _ => prediction.PredictionCategory
        };
    }

    public static string GetConfidenceChip(Prediction prediction)
    {
        return $"{GetConfidenceValue(prediction) * 100m:F1}% confidence";
    }

    public static string GetThresholdChip(Prediction prediction)
    {
        var source = string.IsNullOrWhiteSpace(prediction.ThresholdSource)
            ? "Configured"
            : prediction.ThresholdSource;
        return $"{source} {prediction.ThresholdUsed * 100d:F1}% gate";
    }

    public static string GetSourceChip(Prediction prediction)
    {
        var calibrator = string.IsNullOrWhiteSpace(prediction.CalibratorUsed)
            ? "Bucket"
            : prediction.CalibratorUsed;
        return $"{calibrator} calibrator";
    }

    public static string GetStatusLabel(Prediction prediction, DateTime utcNow)
    {
        if (IsActuallyLive(prediction, utcNow))
        {
            return "Live";
        }

        if (!string.IsNullOrWhiteSpace(prediction.ActualScore))
        {
            return "Finished";
        }

        return "Upcoming";
    }

    public static string BuildPredictionReason(Prediction prediction)
    {
        var confidence = GetConfidenceValue(prediction) * 100m;
        var threshold = prediction.ThresholdUsed * 100d;
        var margin = (double)GetConfidenceValue(prediction) * 100d - threshold;
        var source = string.IsNullOrWhiteSpace(prediction.ThresholdSource)
            ? "configured"
            : prediction.ThresholdSource.ToLowerInvariant();

        if (prediction.PredictionCategory == "StraightWin")
        {
            return $"{prediction.PredictedOutcome} was published because calibrated confidence sits {margin:+0.0;-0.0;0.0} pts above the {source} gate at {confidence:F1}%.";
        }

        return $"{GetPredictionBadgeText(prediction)} cleared the {source} threshold with {confidence:F1}% calibrated confidence and {margin:+0.0;-0.0;0.0} pts of breathing room.";
    }

    public static bool IsActuallyLive(Prediction prediction, DateTime utcNow)
    {
        if (!prediction.IsLive)
        {
            return false;
        }

        return !prediction.MatchDateTime.HasValue || utcNow <= prediction.MatchDateTime.Value.AddMinutes(200);
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
