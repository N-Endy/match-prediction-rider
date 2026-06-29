using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Services;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class CalibrationKpiTests
{
    [Fact]
    public void CalculateStats_ComputesExpectedCalibrationError_FromForecastBuckets()
    {
        var service = new ForecastEvaluationService();

        // 10 forecasts all calibrated at 0.90 but only half occur -> ECE = |0.90 - 0.50| = 0.40.
        var forecasts = Enumerable.Range(0, 10)
            .Select(i => CreateForecast(0.90, 0.90, occurred: i < 5))
            .ToList();

        var stats = service.CalculateStats(Array.Empty<Prediction>(), forecasts);

        Assert.Equal(10, stats.SettledForecasts);
        Assert.Equal(0.40, stats.ExpectedCalibrationError, 6);
        Assert.Equal(0.40, stats.RawExpectedCalibrationError, 6);

        var market = Assert.Single(stats.ForecastMarketStats);
        Assert.Equal(0.40, market.CalibratedExpectedCalibrationError, 6);
        Assert.Equal(0.40, market.RawExpectedCalibrationError, 6);
    }

    [Fact]
    public void CalculateStats_PopulatesConfidenceBandPrecisionRecallF1()
    {
        var service = new ForecastEvaluationService();

        var forecasts = Enumerable.Range(0, 10)
            .Select(i => CreateForecast(0.90, 0.92, occurred: i < 6))
            .ToList();

        var stats = service.CalculateStats(Array.Empty<Prediction>(), forecasts);

        var band = Assert.Single(stats.ConfidenceBandStats);
        Assert.Equal(0.6, band.HitRate, 6);
        Assert.Equal(0.6, band.Precision, 6);
        Assert.Equal(0.6, band.Recall, 6);
        // F1 of equal precision/recall equals that value.
        Assert.Equal(0.6, band.F1Score, 6);
    }

    private static ForecastObservation CreateForecast(double rawProbability, double calibratedProbability, bool occurred)
    {
        return new ForecastObservation
        {
            Date = "12-03-2026",
            Time = "18:00",
            MatchLocalDate = new DateOnly(2026, 3, 12),
            MatchLocalTime = new TimeOnly(18, 0),
            MatchDateTime = new DateTime(2026, 3, 12, 17, 0, 0, DateTimeKind.Utc),
            FixtureKey = Guid.NewGuid().ToString("N"),
            League = "League",
            HomeTeam = Guid.NewGuid().ToString("N"),
            AwayTeam = Guid.NewGuid().ToString("N"),
            Market = PredictionMarket.BothTeamsScore,
            PredictedOutcome = "BTTS",
            RawProbability = rawProbability,
            CalibratedProbability = calibratedProbability,
            CalibratorUsed = "Bucket",
            ThresholdSource = "Configured",
            ThresholdUsed = 0.55,
            OutcomeOccurred = occurred,
            IsSettled = true,
            IsPublished = true,
            CreatedAt = new DateTime(2026, 3, 12, 15, 0, 0, DateTimeKind.Utc)
        };
    }
}
