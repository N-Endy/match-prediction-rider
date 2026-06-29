using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Services;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class ForecastFeatureDiagnosticsTests
{
    [Fact]
    public void CalculateStats_ParsesStoredFeatureContributionsIntoDiagnostics()
    {
        var service = new ForecastEvaluationService();

        const string featureJson = """
        {
            "market": "BothTeamsScore",
            "sourceSignals": { "homeWin": 0.45, "draw": 0.27, "awayWin": 0.28, "over25": 0.55, "bttsYes": 0.61 },
            "modelOutputs": { "btts": 0.63, "over25": 0.58, "under25": 0.42, "homeWin": 0.44, "awayWin": 0.29, "draw": 0.27 },
            "statisticalSignal": { "btts": 0.66, "over25": 0.6, "homeWin": 0.43, "awayWin": 0.3, "draw": 0.27 },
            "statisticalSignalApplied": true
        }
        """;

        var forecast = CreateForecast(0.63, 0.66, occurred: true, featureJson);

        var stats = service.CalculateStats(Array.Empty<Prediction>(), [forecast]);

        var diagnostic = Assert.Single(stats.FeatureDiagnostics);
        Assert.Equal("BTTS", diagnostic.PredictedOutcome);
        Assert.True(diagnostic.StatisticalSignalApplied);
        Assert.True(diagnostic.OutcomeOccurred);
        Assert.Equal(0.63, diagnostic.RawProbability, 6);
        Assert.Equal(0.66, diagnostic.CalibratedProbability, 6);

        Assert.Contains(diagnostic.Contributions, item => item.Group == "Market" && item.Label == "bttsYes" && item.Value == 0.61);
        Assert.Contains(diagnostic.Contributions, item => item.Group == "Model" && item.Label == "btts" && item.Value == 0.63);
        Assert.Contains(diagnostic.Contributions, item => item.Group == "Statistical" && item.Label == "btts" && item.Value == 0.66);
    }

    [Fact]
    public void CalculateStats_WithMalformedFeatureJson_YieldsDiagnosticWithoutContributions()
    {
        var service = new ForecastEvaluationService();
        var forecast = CreateForecast(0.6, 0.6, occurred: false, "not-json");

        var stats = service.CalculateStats(Array.Empty<Prediction>(), [forecast]);

        var diagnostic = Assert.Single(stats.FeatureDiagnostics);
        Assert.False(diagnostic.StatisticalSignalApplied);
        Assert.Empty(diagnostic.Contributions);
    }

    private static ForecastObservation CreateForecast(
        double rawProbability,
        double calibratedProbability,
        bool occurred,
        string featureContributionsJson)
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
            FeatureContributionsJson = featureContributionsJson,
            OutcomeOccurred = occurred,
            IsSettled = true,
            IsPublished = true,
            CreatedAt = new DateTime(2026, 3, 12, 15, 0, 0, DateTimeKind.Utc)
        };
    }
}
