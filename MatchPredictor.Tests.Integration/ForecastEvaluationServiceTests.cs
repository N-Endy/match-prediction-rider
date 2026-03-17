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
                MatchLocalDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddHours(18)),
                MatchLocalTime = new TimeOnly(18, 0),
                MatchDateTime = DateTime.UtcNow.Date.AddHours(17),
                FixtureKey = "league|fixture|one",
                League = "League",
                HomeTeam = "Home",
                AwayTeam = "Away",
                PredictionCategory = "BothTeamsScore",
                PredictedOutcome = "BTTS",
                ActualOutcome = "BTTS",
                IsLive = false,
                ConfidenceScore = 0.80m,
                CreatedAt = DateTime.UtcNow.Date.AddHours(15)
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

    [Fact]
    public void CalculateStats_UsesPointInTimePredictionAndForecastRevisions()
    {
        var service = new ForecastEvaluationService();
        var kickoff = DateTime.UtcNow.Date.AddHours(18);
        var localDate = DateOnly.FromDateTime(kickoff);
        const string fixtureKey = "league|alpha|beta";

        var predictions = new[]
        {
            new Prediction
            {
                MatchLocalDate = localDate,
                MatchLocalTime = new TimeOnly(18, 0),
                MatchDateTime = kickoff,
                FixtureKey = fixtureKey,
                League = "League",
                HomeTeam = "Alpha",
                AwayTeam = "Beta",
                PredictionCategory = "BothTeamsScore",
                PredictedOutcome = "BTTS",
                ActualOutcome = "BTTS",
                ConfidenceScore = 0.62m,
                IsLive = false,
                RevisionNumber = 1,
                CreatedAt = kickoff.AddHours(-2)
            },
            new Prediction
            {
                MatchLocalDate = localDate,
                MatchLocalTime = new TimeOnly(18, 0),
                MatchDateTime = kickoff,
                FixtureKey = fixtureKey,
                League = "League",
                HomeTeam = "Alpha",
                AwayTeam = "Beta",
                PredictionCategory = "BothTeamsScore",
                PredictedOutcome = "No BTTS",
                ActualOutcome = "BTTS",
                ConfidenceScore = 0.90m,
                IsLive = false,
                RevisionNumber = 2,
                CreatedAt = kickoff.AddHours(1)
            }
        };

        var forecasts = new[]
        {
            new ForecastObservation
            {
                MatchLocalDate = localDate,
                MatchLocalTime = new TimeOnly(18, 0),
                MatchDateTime = kickoff,
                FixtureKey = fixtureKey,
                League = "League",
                HomeTeam = "Alpha",
                AwayTeam = "Beta",
                Market = PredictionMarket.BothTeamsScore,
                PredictedOutcome = "BTTS",
                RawProbability = 0.62,
                CalibratedProbability = 0.66,
                CalibratorUsed = "Bucket",
                ThresholdSource = "Configured",
                ThresholdUsed = 0.55,
                OutcomeOccurred = true,
                IsSettled = true,
                IsPublished = true,
                RevisionNumber = 1,
                CreatedAt = kickoff.AddHours(-2)
            },
            new ForecastObservation
            {
                MatchLocalDate = localDate,
                MatchLocalTime = new TimeOnly(18, 0),
                MatchDateTime = kickoff,
                FixtureKey = fixtureKey,
                League = "League",
                HomeTeam = "Alpha",
                AwayTeam = "Beta",
                Market = PredictionMarket.BothTeamsScore,
                PredictedOutcome = "No BTTS",
                RawProbability = 0.12,
                CalibratedProbability = 0.18,
                CalibratorUsed = "Beta",
                ThresholdSource = "Tuned",
                ThresholdUsed = 0.60,
                OutcomeOccurred = false,
                IsSettled = true,
                IsPublished = true,
                RevisionNumber = 2,
                CreatedAt = kickoff.AddHours(1)
            }
        };

        var stats = service.CalculateStats(predictions, forecasts);
        var market = Assert.Single(stats.ForecastMarketStats);

        Assert.Equal(1, stats.TotalPredictions);
        Assert.Equal(1, stats.CompletedPredictions);
        Assert.Equal(1, stats.CorrectPredictions);
        Assert.Equal(1, stats.SettledForecasts);
        Assert.Contains(market.CalibratorEraStats, era => era.Era == "Bucket" && era.Count == 1);
        Assert.DoesNotContain(market.CalibratorEraStats, era => era.Era == "Beta");
    }

    [Fact]
    public void CalculateStats_DerivesCompletedDrawResultFromAgedScoreWhenOutcomeIsMissing()
    {
        var service = new ForecastEvaluationService();
        var kickoff = DateTime.UtcNow.AddHours(-6);
        var localDate = DateOnly.FromDateTime(kickoff);

        var predictions = new[]
        {
            new Prediction
            {
                MatchLocalDate = localDate,
                MatchLocalTime = TimeOnly.FromDateTime(kickoff),
                MatchDateTime = kickoff,
                FixtureKey = "league|umecit|union-cocle",
                League = "League",
                HomeTeam = "UMECIT",
                AwayTeam = "Union Cocle",
                PredictionCategory = "Draw",
                PredictedOutcome = "Draw",
                ActualScore = "1:1",
                ActualOutcome = null,
                IsLive = true,
                ConfidenceScore = 0.35m,
                CreatedAt = kickoff.AddHours(-2)
            },
            new Prediction
            {
                MatchLocalDate = localDate,
                MatchLocalTime = TimeOnly.FromDateTime(kickoff),
                MatchDateTime = kickoff,
                FixtureKey = "league|alpha|beta",
                League = "League",
                HomeTeam = "Alpha",
                AwayTeam = "Beta",
                PredictionCategory = "Draw",
                PredictedOutcome = "Draw",
                ActualScore = "2:1",
                ActualOutcome = "Not Draw",
                IsLive = false,
                ConfidenceScore = 0.31m,
                CreatedAt = kickoff.AddHours(-2)
            },
            new Prediction
            {
                MatchLocalDate = localDate,
                MatchLocalTime = TimeOnly.FromDateTime(kickoff),
                MatchDateTime = kickoff,
                FixtureKey = "league|gamma|delta",
                League = "League",
                HomeTeam = "Gamma",
                AwayTeam = "Delta",
                PredictionCategory = "Draw",
                PredictedOutcome = "Draw",
                ActualScore = "3:1",
                ActualOutcome = "Not Draw",
                IsLive = false,
                ConfidenceScore = 0.32m,
                CreatedAt = kickoff.AddHours(-2)
            },
            new Prediction
            {
                MatchLocalDate = localDate,
                MatchLocalTime = TimeOnly.FromDateTime(kickoff),
                MatchDateTime = kickoff,
                FixtureKey = "league|epsilon|zeta",
                League = "League",
                HomeTeam = "Epsilon",
                AwayTeam = "Zeta",
                PredictionCategory = "Draw",
                PredictedOutcome = "Draw",
                ActualScore = "0:2",
                ActualOutcome = "Not Draw",
                IsLive = false,
                ConfidenceScore = 0.30m,
                CreatedAt = kickoff.AddHours(-2)
            }
        };

        var stats = service.CalculateStats(predictions, []);
        var drawStats = Assert.Single(stats.CategoryStats.Values);

        Assert.Equal(4, stats.CompletedPredictions);
        Assert.Equal(1, stats.CorrectPredictions);
        Assert.Equal("Draw", drawStats.Category);
        Assert.Equal(4, drawStats.Total);
        Assert.Equal(1, drawStats.Correct);
        Assert.Equal(0.25, drawStats.Accuracy, 5);
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
            CalibratorUsed = calibratorUsed,
            ThresholdSource = thresholdSource,
            ThresholdUsed = 0.55,
            OutcomeOccurred = occurred,
            IsSettled = true,
            IsPublished = isPublished,
            CreatedAt = new DateTime(2026, 3, 12, 15, 0, 0, DateTimeKind.Utc)
        };
    }
}
