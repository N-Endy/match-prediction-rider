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
            return parsedTime.ToString("h:mm tt", CultureInfo.InvariantCulture);
        }

        if (prediction.MatchDateTime.HasValue)
        {
            return DateTimeProvider.ConvertUtcToLocal(prediction.MatchDateTime.Value).ToString("h:mm tt", CultureInfo.InvariantCulture);
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
            "MatchWinner" when string.Equals(prediction.PredictedOutcome, "Away Win", StringComparison.OrdinalIgnoreCase) => "mp-badge-win-away",
            "MatchWinner" => "mp-badge-win-home",
            "OverUnderSets" when string.Equals(prediction.PredictedOutcome, "Under 2.5 Sets", StringComparison.OrdinalIgnoreCase) => "mp-badge-under",
            "OverUnderSets" => "mp-badge-over",
            "SetHandicap" => "mp-badge-combined",
            _ => "mp-badge-combined"
        };
    }

    public static string GetPredictionBadgeText(Prediction prediction)
    {
        return prediction.PredictionCategory switch
        {
            "MatchWinner" => prediction.PredictedOutcome ?? "Match Winner",
            "OverUnderSets" => prediction.PredictedOutcome ?? "Set Total",
            "SetHandicap" => "Set Handicap",
            _ => prediction.PredictedOutcome ?? prediction.PredictionCategory
        };
    }

    public static string GetCartMarket(Prediction prediction)
    {
        return prediction.PredictionCategory switch
        {
            "MatchWinner" => "MatchWinner",
            "OverUnderSets" => "OverUnderSets",
            "SetHandicap" => "SetHandicap",
            _ => prediction.PredictionCategory
        };
    }

    public static bool SupportsBetslip(Prediction prediction)
    {
        return prediction.PredictionCategory is "MatchWinner" or "OverUnderSets" or "SetHandicap";
    }

    public static bool IsSportyBetBookable(Prediction prediction)
    {
        return string.Equals(prediction.PredictionCategory, "MatchWinner", StringComparison.OrdinalIgnoreCase) &&
               (string.Equals(prediction.PredictedOutcome, "Home Win", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(prediction.PredictedOutcome, "Away Win", StringComparison.OrdinalIgnoreCase));
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

        return prediction.PredictionCategory switch
        {
            "MatchWinner" => $"{prediction.PredictedOutcome} cleared the {source} match-winner gate by {margin:+0.0;-0.0;0.0} pts at {confidence:F1}% calibrated confidence.",
            "OverUnderSets" => $"{prediction.PredictedOutcome} stayed above the {source} totals gate with {confidence:F1}% calibrated confidence and {margin:+0.0;-0.0;0.0} pts of room.",
            "SetHandicap" => $"{prediction.PredictedOutcome} was published because the handicap edge remained {margin:+0.0;-0.0;0.0} pts above the {source} threshold.",
            _ => $"{prediction.PredictedOutcome} cleared the {source} threshold with {confidence:F1}% calibrated confidence."
        };
    }

    public static bool IsActuallyLive(Prediction prediction, DateTime utcNow)
    {
        if (!prediction.IsLive)
        {
            return false;
        }

        return !prediction.MatchDateTime.HasValue || utcNow <= prediction.MatchDateTime.Value.AddHours(6);
    }

    public static bool IsLivePredictionCorrect(Prediction prediction)
    {
        return prediction.PredictionCategory switch
        {
            "OverUnderSets" => IsPredictionCorrect(prediction),
            _ => false
        };
    }

    public static string GetScoreClass(Prediction prediction, DateTime utcNow)
    {
        var isActuallyLive = IsActuallyLive(prediction, utcNow);

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
        if (TryParseSetScore(prediction.ActualScore, out var homeSetsWon, out var awaySetsWon))
        {
            return prediction.PredictionCategory switch
            {
                "MatchWinner" => DoesMatchWinnerPredictionMatch(prediction.PredictedOutcome, homeSetsWon, awaySetsWon),
                "OverUnderSets" => DoesSetTotalPredictionMatch(prediction.PredictedOutcome, homeSetsWon, awaySetsWon),
                "SetHandicap" => DoesSetHandicapPredictionMatch(prediction.PredictedOutcome, homeSetsWon, awaySetsWon),
                _ => OutcomesMatch(prediction.PredictedOutcome, prediction.ActualOutcome)
            };
        }

        return OutcomesMatch(prediction.PredictedOutcome, prediction.ActualOutcome);
    }

    public static string FormatOutcome(Prediction prediction)
    {
        return prediction.PredictedOutcome;
    }

    private static bool TryParseSetScore(string? score, out int homeSetsWon, out int awaySetsWon)
    {
        homeSetsWon = 0;
        awaySetsWon = 0;

        if (string.IsNullOrWhiteSpace(score))
        {
            return false;
        }

        var matches = ScoreRegex()
            .Matches(score)
            .Select(match => match.Value)
            .ToArray();

        return matches.Length >= 2 &&
               int.TryParse(matches[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out homeSetsWon) &&
               int.TryParse(matches[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out awaySetsWon);
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex ScoreRegex();

    private static bool DoesMatchWinnerPredictionMatch(string? predictedOutcome, int homeSetsWon, int awaySetsWon)
    {
        return NormalizeOutcome(predictedOutcome) switch
        {
            "home win" => homeSetsWon > awaySetsWon,
            "away win" => awaySetsWon > homeSetsWon,
            _ => false
        };
    }

    private static bool DoesSetTotalPredictionMatch(string? predictedOutcome, int homeSetsWon, int awaySetsWon)
    {
        var isOver = homeSetsWon + awaySetsWon > 2;

        return NormalizeOutcome(predictedOutcome) switch
        {
            "over 2.5 sets" => isOver,
            "under 2.5 sets" => !isOver,
            _ => false
        };
    }

    private static bool DoesSetHandicapPredictionMatch(string? predictedOutcome, int homeSetsWon, int awaySetsWon)
    {
        if (!TryParseHandicapOutcome(predictedOutcome, out var side, out var line))
        {
            return false;
        }

        return side.Equals("Home", StringComparison.OrdinalIgnoreCase)
            ? homeSetsWon + line > awaySetsWon
            : awaySetsWon + line > homeSetsWon;
    }

    private static bool TryParseHandicapOutcome(string? predictedOutcome, out string side, out double line)
    {
        side = string.Empty;
        line = 0d;

        if (string.IsNullOrWhiteSpace(predictedOutcome))
        {
            return false;
        }

        var match = HandicapRegex().Match(predictedOutcome.Trim());
        if (match.Success == false)
        {
            return false;
        }

        side = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(match.Groups["side"].Value.ToLowerInvariant());
        return double.TryParse(match.Groups["line"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out line);
    }

    [GeneratedRegex(@"^(?<side>Home|Away)\s+(?<line>[+-]?\d+(?:\.\d+)?)\s+Sets$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HandicapRegex();

    private static bool OutcomesMatch(string? predictedOutcome, string? actualOutcome)
    {
        return string.Equals(
            NormalizeOutcome(predictedOutcome),
            NormalizeOutcome(actualOutcome),
            StringComparison.Ordinal);
    }

    private static string NormalizeOutcome(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : WhitespaceRegex().Replace(value.Trim().ToLowerInvariant(), " ");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
