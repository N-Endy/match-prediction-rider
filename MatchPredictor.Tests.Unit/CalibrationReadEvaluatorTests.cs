using MatchPredictor.Domain.Helpers;
using MatchPredictor.Domain.Models;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class CalibrationReadEvaluatorTests
{
    [Fact]
    public void Evaluate_AvoidsTinySamples()
    {
        var read = CalibrationReadEvaluator.Evaluate(4, brierScore: 0.03, logLoss: 0.10);

        Assert.Equal(CalibrationReadKind.Avoid, read.Kind);
        Assert.Equal(CalibrationReadEvaluator.AvoidCssClass, CalibrationReadEvaluator.CssClass(read));
    }

    [Fact]
    public void Evaluate_MarksLightSamplesAsCautionEvenWhenBrierLooksGreat()
    {
        var read = CalibrationReadEvaluator.Evaluate(
            12,
            brierScore: 0.159,
            logLoss: 0.45,
            expectedCalibrationError: 0.04,
            uncertainty: 0.18);

        Assert.Equal(CalibrationReadKind.Caution, read.Kind);
        Assert.Contains("sample is still light", read.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_TreatsWellCalibratedBttsLikeMarketAsStrong()
    {
        // Published BTTS today: 65.5% (19/29), Brier 0.224. Full-card BTTS sits near 50%,
        // so uncertainty around 0.24 is expected and should not be punished.
        var read = CalibrationReadEvaluator.Evaluate(
            29,
            brierScore: 0.224,
            logLoss: 0.64,
            expectedCalibrationError: 0.05,
            uncertainty: 0.24);

        Assert.Equal(CalibrationReadKind.Strong, read.Kind);
    }

    [Fact]
    public void Evaluate_TreatsPolarizedHomeWinAsStrong()
    {
        var read = CalibrationReadEvaluator.Evaluate(
            40,
            brierScore: 0.180,
            logLoss: 0.52,
            expectedCalibrationError: 0.03,
            uncertainty: 0.20);

        Assert.Equal(CalibrationReadKind.Strong, read.Kind);
    }

    [Fact]
    public void Evaluate_DoesNotCallBttsCautionJustBecauseRawBrierExceedsZeroPointTwoTwo()
    {
        var oldRuleWouldCaution = 0.224 >= 0.22;
        Assert.True(oldRuleWouldCaution);

        var read = CalibrationReadEvaluator.Evaluate(
            40,
            brierScore: 0.224,
            logLoss: 0.63,
            expectedCalibrationError: 0.04,
            uncertainty: 0.247);

        Assert.Equal(CalibrationReadKind.Strong, read.Kind);
    }

    [Fact]
    public void Evaluate_AvoidsWhenReliabilityIsLooseEvenIfBrierLooksLow()
    {
        var read = CalibrationReadEvaluator.Evaluate(
            40,
            brierScore: 0.18,
            logLoss: 0.55,
            expectedCalibrationError: 0.15,
            uncertainty: 0.22);

        Assert.Equal(CalibrationReadKind.Avoid, read.Kind);
    }

    [Fact]
    public void Evaluate_AvoidsWhenBrierIsWorseThanTheMarketBaseRate()
    {
        var read = CalibrationReadEvaluator.Evaluate(
            40,
            brierScore: 0.30,
            logLoss: 0.72,
            expectedCalibrationError: 0.06,
            uncertainty: 0.22);

        Assert.Equal(CalibrationReadKind.Avoid, read.Kind);
    }
}
