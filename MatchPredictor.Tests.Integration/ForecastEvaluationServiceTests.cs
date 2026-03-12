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
            CreateForecast(0.80, 0.75, true),
            CreateForecast(0.80, 0.75, true),
            CreateForecast(0.20, 0.25, false),
            CreateForecast(0.20, 0.25, false)
        };

        var stats = service.CalculateStats(predictions, forecasts);
        var market = Assert.Single(stats.ForecastMarketStats);

        Assert.Equal(4, stats.SettledForecasts);
        Assert.Equal(2, market.RawReliabilityCurve.Count);
        Assert.Equal(2, market.CalibratedReliabilityCurve.Count);
        Assert.True(Math.Abs(
            market.RawBrierScore -
            (market.RawDecomposition.Reliability - market.RawDecomposition.Resolution + market.RawDecomposition.Uncertainty)) < 0.000001);
        Assert.True(Math.Abs(
            market.CalibratedBrierScore -
            (market.CalibratedDecomposition.Reliability - market.CalibratedDecomposition.Resolution + market.CalibratedDecomposition.Uncertainty)) < 0.000001);
    }

    private static ForecastObservation CreateForecast(double rawProbability, double calibratedProbability, bool occurred)
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
            OutcomeOccurred = occurred,
            IsSettled = true
        };
    }
}
