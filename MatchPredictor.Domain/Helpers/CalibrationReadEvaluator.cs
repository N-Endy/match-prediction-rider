using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Helpers;

public static class CalibrationReadEvaluator
{
    public const string StrongCssClass = "mp-read-badge-strong";
    public const string CautionCssClass = "mp-read-badge-caution";
    public const string AvoidCssClass = "mp-read-badge-avoid";

    public static string CssClass(CalibrationReadKind kind) => kind switch
    {
        CalibrationReadKind.Strong => StrongCssClass,
        CalibrationReadKind.Avoid => AvoidCssClass,
        _ => CautionCssClass
    };

    public static string CssClass(CalibrationRead read) => CssClass(read.Kind);

    /// <summary>
    /// Grades probability quality for a settled forecast window.
    /// Raw Brier is a poor standalone gate: BTTS/totals sit near 50% so even a well-calibrated
    /// market has Brier around 0.24, while Home/Away can look "Strong" just because those
    /// probabilities are more polarized. ECE and Brier vs uncertainty measure calibration instead.
    /// </summary>
    public static CalibrationRead Evaluate(
        int sampleCount,
        double brierScore,
        double logLoss,
        double? expectedCalibrationError = null,
        double? uncertainty = null)
    {
        if (sampleCount < 8)
        {
            return new CalibrationRead(
                CalibrationReadKind.Avoid,
                "Too little settled history to trust this segment on its own yet.");
        }

        var poorlyCalibrated = IsPoorlyCalibrated(brierScore, logLoss, expectedCalibrationError, uncertainty);
        if (sampleCount < 20)
        {
            return poorlyCalibrated
                ? new CalibrationRead(
                    CalibrationReadKind.Caution,
                    "Sample is still light, and the probabilities look noisier than this market's base rate.")
                : new CalibrationRead(
                    CalibrationReadKind.Caution,
                    "Signal is starting to form, but the sample is still light.");
        }

        if (poorlyCalibrated)
        {
            return new CalibrationRead(
                CalibrationReadKind.Avoid,
                "Forecast quality is currently too loose for normal staking.");
        }

        if (IsCleanlyCalibrated(brierScore, logLoss, expectedCalibrationError, uncertainty))
        {
            return sampleCount >= 40
                ? new CalibrationRead(
                    CalibrationReadKind.Strong,
                    "Healthy sample and clean enough probability quality to lean on.")
                : new CalibrationRead(
                    CalibrationReadKind.Strong,
                    "Settled probabilities in this window look well calibrated.");
        }

        return new CalibrationRead(
            CalibrationReadKind.Caution,
            "Playable, but keep stake size measured until the edge looks cleaner.");
    }

    private static bool IsPoorlyCalibrated(
        double brierScore,
        double logLoss,
        double? expectedCalibrationError,
        double? uncertainty)
    {
        if (logLoss >= 0.75)
        {
            return true;
        }

        if (expectedCalibrationError is >= 0.12)
        {
            return true;
        }

        if (uncertainty is > 0)
        {
            return brierScore >= uncertainty.Value + 0.06;
        }

        return brierScore >= 0.28;
    }

    private static bool IsCleanlyCalibrated(
        double brierScore,
        double logLoss,
        double? expectedCalibrationError,
        double? uncertainty)
    {
        if (logLoss > 0.70)
        {
            return false;
        }

        if (expectedCalibrationError is > 0.08)
        {
            return false;
        }

        if (uncertainty is > 0)
        {
            return brierScore <= uncertainty.Value + 0.02;
        }

        return brierScore <= 0.25;
    }
}
