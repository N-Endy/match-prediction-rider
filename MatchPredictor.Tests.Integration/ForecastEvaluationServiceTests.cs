using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Services;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class ForecastEvaluationServiceTests
{
    [Fact]
    public void CalculateStats_ComputesDecompositionAndReliabilityCurves_PerMarket()
    {
        var service = new ForecastEvaluationService();

        var predictions = new[]
        {
            new Prediction
            {
                PredictionCategory = "BothTeamsScore",
                PredictedOutcome = "BTTS",
                ActualOutcome = "BTTS",
                IsLive = false,
                ConfidenceScore = 0.80m
            }
        };

        var forecasts = new[]
        {
            CreateForecast(0.80, 0.75, true, calibratorUsed: "Bucket", thresholdSource: "Configured", isPublished: true),
            CreateForecast(0.80, 0.75, true, calibratorUsed: "Bucket", thresholdSource: "Configured", isPublished: true),
            CreateForecast(0.20, 0.25, false, calibratorUsed: "Beta", thresholdSource: "Tuned", isPublished: true),
            CreateForecast(0.20, 0.25, false, calibratorUsed: "Beta", thresholdSource: "Tuned", isPublished: true)
        };

        var stats = service.CalculateStats(predictions, forecasts);
        var market = Assert.Single(stats.ForecastMarketStats);

        Assert.Equal(4, stats.SettledForecasts);
        Assert.Equal(2, market.RawReliabilityCurve.Count);
        Assert.Equal(2, market.CalibratedReliabilityCurve.Count);
        Assert.Equal(2, market.CalibratorEraStats.Count);
        Assert.Equal(2, market.ThresholdEraStats.Count);
        Assert.Contains(market.CalibratorEraStats, era => era.Era == "Bucket" && era.Count == 2);
        Assert.Contains(market.CalibratorEraStats, era => era.Era == "Beta" && era.Count == 2);
        Assert.Contains(market.ThresholdEraStats, era => era.Era == "Configured" && era.Count == 2);
        Assert.Contains(market.ThresholdEraStats, era => era.Era == "Tuned" && era.Count == 2);
        Assert.True(Math.Abs(
            market.RawBrierScore -
            (market.RawDecomposition.Reliability - market.RawDecomposition.Resolution + market.RawDecomposition.Uncertainty)) < 0.000001);
        Assert.True(Math.Abs(
            market.CalibratedBrierScore -
            (market.CalibratedDecomposition.Reliability - market.CalibratedDecomposition.Resolution + market.CalibratedDecomposition.Uncertainty)) < 0.000001);
    }

    private static ForecastObservation CreateForecast(
        double rawProbability,
        double calibratedProbability,
        bool occurred,
        string calibratorUsed,
        string thresholdSource,
        bool isPublished)
    {
        return new ForecastObservation
        {
            Date = "12-03-2026",
            Time = "18:00",
            League = "League",
            HomeTeam = Guid.NewGuid().ToString("N"),
            AwayTeam = Guid.NewGuid().ToString("N"),
            Market = PredictionMarket.BothTeamsScore,
            PredictedOutcome = "BTTS",
            RawProbability = rawProbability,
            CalibratedProbability = calibratedProbability,
            CalibratorUsed = calibratorUsed,
            ThresholdSource = thresholdSource,
            ThresholdUsed = 0.55,
            OutcomeOccurred = occurred,
            IsSettled = true,
            IsPublished = isPublished
        };
    }
}
