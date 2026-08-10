using System.Globalization;
using MatchPredictor.Application.Helpers;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Utils;

namespace MatchPredictor.Web.Helpers;

public static class PredictionDisplayHelper
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

        var normalized = runReason.Trim().ToLowerInvariant().Replace('_', '-');
        return normalized switch
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

    public static bool IsActuallyLive(Prediction prediction, DateTime utcNow) =>
        PredictionScoreClassHelper.IsActuallyLive(prediction, utcNow);

    public static bool IsLivePredictionCorrect(Prediction prediction) =>
        PredictionScoreClassHelper.IsLivePredictionCorrect(prediction);

    public static string GetScoreClass(Prediction prediction, DateTime utcNow) =>
        PredictionScoreClassHelper.GetScoreClass(prediction, utcNow);

    public static bool IsPredictionCorrect(Prediction prediction) =>
        PredictionScoreClassHelper.IsPredictionCorrect(prediction);
}
