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
    public void CalculateStats_ExcludesLegacyDrawRows_FromAnalyticsDashboardStats()
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
                FixtureKey = "league|under-home|under-away",
                League = "League",
                HomeTeam = "Under Home",
                AwayTeam = "Under Away",
                PredictionCategory = "Under2.5Goals",
                PredictedOutcome = "Under 2.5",
                ActualScore = "1:1",
                ActualOutcome = "Under 2.5",
                IsLive = false,
                ConfidenceScore = 0.63m,
                CreatedAt = kickoff.AddHours(-2)
            },
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
        };

        var forecasts = new[]
        {
            CreateForecast(
                rawProbability: 0.66,
                calibratedProbability: 0.63,
                occurred: true,
                calibratorUsed: "Bucket",
                thresholdSource: "Configured",
                isPublished: true,
                market: PredictionMarket.Under25Goals,
                predictedOutcome: "Under 2.5"),
            CreateForecast(
                rawProbability: 0.34,
                calibratedProbability: 0.31,
                occurred: false,
                calibratorUsed: "Bucket",
                thresholdSource: "Configured",
                isPublished: true,
                market: PredictionMarket.Draw,
                predictedOutcome: "Draw")
        };

        var stats = service.CalculateStats(predictions, forecasts);
        var categoryStats = Assert.Single(stats.CategoryStats.Values);
        var marketStats = Assert.Single(stats.ForecastMarketStats);

        Assert.Equal(1, stats.TotalPredictions);
        Assert.Equal(1, stats.CompletedPredictions);
        Assert.Equal(1, stats.CorrectPredictions);
        Assert.Equal("Under2.5Goals", categoryStats.Category);
        Assert.Equal("Under 2.5", categoryStats.DisplayName);
        Assert.Equal(1, categoryStats.Total);
        Assert.Equal(1, categoryStats.Correct);
        Assert.Equal(1.0, categoryStats.Accuracy, 5);
        Assert.Equal(1, stats.SettledForecasts);
        Assert.Equal(PredictionMarket.Under25Goals, marketStats.Market);
    }

    [Fact]
    public void CalculateStats_IncludesUnpublishedSettledForecasts_InCalibrationMetrics()
    {
        var service = new ForecastEvaluationService();
        var predictions = new[]
        {
            new Prediction
            {
                MatchLocalDate = new DateOnly(2026, 3, 12),
                MatchLocalTime = new TimeOnly(18, 0),
                MatchDateTime = new DateTime(2026, 3, 12, 17, 0, 0, DateTimeKind.Utc),
                FixtureKey = "league|published-home|published-away",
                League = "League",
                HomeTeam = "Published Home",
                AwayTeam = "Published Away",
                PredictionCategory = "BothTeamsScore",
                PredictedOutcome = "BTTS",
                ActualOutcome = "BTTS",
                IsLive = false,
                ConfidenceScore = 0.80m,
                CreatedAt = new DateTime(2026, 3, 12, 15, 0, 0, DateTimeKind.Utc)
            }
        };

        var forecasts = new[]
        {
            CreateForecast(0.80, 0.80, occurred: true, calibratorUsed: "Bucket", thresholdSource: "Configured", isPublished: true),
            CreateForecast(0.20, 0.20, occurred: false, calibratorUsed: "Bucket", thresholdSource: "Configured", isPublished: false)
        };

        var stats = service.CalculateStats(predictions, forecasts);

        Assert.Equal(1, stats.CompletedPredictions);
        Assert.Equal(1.0, stats.Precision, 5);
        Assert.Equal(2, stats.SettledForecasts);
        Assert.Equal(0.5, stats.ForecastHitRate, 5);
        Assert.True(stats.BrierScore > 0);
    }

    [Fact]
    public void CalculateStats_KeepsIsotonicAsItsOwnCalibratorEra()
    {
        var service = new ForecastEvaluationService();
        var forecasts = new[]
        {
            CreateForecast(0.70, 0.68, occurred: true, calibratorUsed: "Isotonic", thresholdSource: "Configured", isPublished: false),
            CreateForecast(0.40, 0.42, occurred: false, calibratorUsed: "Bucket", thresholdSource: "Configured", isPublished: false)
        };

        var stats = service.CalculateStats([], forecasts);
        var market = Assert.Single(stats.ForecastMarketStats);

        Assert.Contains(market.CalibratorEraStats, era => era.Era == "Isotonic" && era.Count == 1);
        Assert.Contains(market.CalibratorEraStats, era => era.Era == "Bucket" && era.Count == 1);
        Assert.DoesNotContain(
            market.CalibratorEraStats,
            era => era.Era == "Bucket" && era.Count == 2);
    }

    [Fact]
    public void CalculateStats_DoesNotSettleLivePicks_FromScoreAfterKickoffGrace()
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
                FixtureKey = "league|live-home|live-away",
                League = "League",
                HomeTeam = "Live Home",
                AwayTeam = "Live Away",
                PredictionCategory = "BothTeamsScore",
                PredictedOutcome = "BTTS",
                ActualScore = "1:1",
                ActualOutcome = null,
                IsLive = true,
                WasPublished = true,
                ConfidenceScore = 0.72m,
                CreatedAt = kickoff.AddHours(-2)
            }
        };

        var stats = service.CalculateStats(predictions, Array.Empty<ForecastObservation>());

        Assert.Equal(1, stats.TotalPredictions);
        Assert.Equal(0, stats.CompletedPredictions);
        Assert.Equal(0, stats.CorrectPredictions);
        Assert.Equal(0.0, stats.OverallAccuracy);
        Assert.Empty(stats.CategoryStats);
    }

    [Fact]
    public void CalculateStats_UsesCompletedPicksOnly_ForOverallAccuracyAndCategoryBrier()
    {
        var service = new ForecastEvaluationService();
        var kickoff = DateTime.UtcNow.AddHours(-6);
        var upcomingKickoff = DateTime.UtcNow.AddHours(3);
        var localDate = DateOnly.FromDateTime(kickoff);

        var predictions = new[]
        {
            new Prediction
            {
                MatchLocalDate = localDate,
                MatchLocalTime = TimeOnly.FromDateTime(kickoff),
                MatchDateTime = kickoff,
                FixtureKey = "league|settled-home|settled-away",
                League = "League",
                HomeTeam = "Settled Home",
                AwayTeam = "Settled Away",
                PredictionCategory = "BothTeamsScore",
                PredictedOutcome = "BTTS",
                ActualScore = "1:1",
                IsLive = false,
                WasPublished = true,
                ConfidenceScore = 0.80m,
                CreatedAt = kickoff.AddHours(-2)
            },
            new Prediction
            {
                MatchLocalDate = localDate,
                MatchLocalTime = TimeOnly.FromDateTime(upcomingKickoff),
                MatchDateTime = upcomingKickoff,
                FixtureKey = "league|pending-home|pending-away",
                League = "League",
                HomeTeam = "Pending Home",
                AwayTeam = "Pending Away",
                PredictionCategory = "BothTeamsScore",
                PredictedOutcome = "BTTS",
                ActualScore = null,
                IsLive = false,
                WasPublished = true,
                ConfidenceScore = 0.90m,
                CreatedAt = upcomingKickoff.AddHours(-2)
            }
        };

        var stats = service.CalculateStats(predictions, Array.Empty<ForecastObservation>());
        var category = Assert.Single(stats.CategoryStats.Values);

        Assert.Equal(2, stats.TotalPredictions);
        Assert.Equal(1, stats.CompletedPredictions);
        Assert.Equal(1, stats.CorrectPredictions);
        Assert.Equal(1.0, stats.OverallAccuracy, 5);
        Assert.Equal("BTTS", category.DisplayName);
        Assert.Equal(1, category.Total);
        Assert.Equal(0.04, category.BrierScore, 5);
    }

    [Fact]
    public void CalculateStats_ClampsCategoryBrierProbability()
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
                FixtureKey = "league|clamp-home|clamp-away",
                League = "League",
                HomeTeam = "Clamp Home",
                AwayTeam = "Clamp Away",
                PredictionCategory = "Over2.5Goals",
                PredictedOutcome = "Over 2.5",
                ActualScore = "2:1",
                IsLive = false,
                WasPublished = true,
                ConfidenceScore = 1.50m,
                CreatedAt = kickoff.AddHours(-2)
            }
        };

        var stats = service.CalculateStats(predictions, Array.Empty<ForecastObservation>());
        var category = Assert.Single(stats.CategoryStats.Values);

        Assert.Equal(0.0, category.BrierScore, 5);
        Assert.Equal("Over 2.5", category.DisplayName);
    }

    [Fact]
    public void CalculateStats_ExcludesUnpublishedPredictions_FromPublishedPickStats()
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
                FixtureKey = "league|published-home|published-away",
                League = "League",
                HomeTeam = "Published Home",
                AwayTeam = "Published Away",
                PredictionCategory = "BothTeamsScore",
                PredictedOutcome = "BTTS",
                ActualScore = "1:1",
                IsLive = false,
                WasPublished = true,
                ConfidenceScore = 0.70m,
                CreatedAt = kickoff.AddHours(-2)
            },
            new Prediction
            {
                MatchLocalDate = localDate,
                MatchLocalTime = TimeOnly.FromDateTime(kickoff),
                MatchDateTime = kickoff,
                FixtureKey = "league|unpublished-home|unpublished-away",
                League = "League",
                HomeTeam = "Unpublished Home",
                AwayTeam = "Unpublished Away",
                PredictionCategory = "Over2.5Goals",
                PredictedOutcome = "Over 2.5",
                ActualScore = "2:1",
                IsLive = false,
                WasPublished = false,
                ConfidenceScore = 0.80m,
                CreatedAt = kickoff.AddHours(-2)
            }
        };

        var stats = service.CalculateStats(predictions, Array.Empty<ForecastObservation>());

        Assert.Equal(1, stats.TotalPredictions);
        Assert.Equal(1, stats.CompletedPredictions);
        Assert.Equal(1, stats.CorrectPredictions);
        Assert.Single(stats.CategoryStats);
        Assert.True(stats.CategoryStats.ContainsKey("BothTeamsScore"));
    }

    private static ForecastObservation CreateForecast(
        double rawProbability,
        double calibratedProbability,
        bool occurred,
        string calibratorUsed,
        string thresholdSource,
        bool isPublished,
        PredictionMarket market = PredictionMarket.BothTeamsScore,
        string predictedOutcome = "BTTS")
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
            Market = market,
            PredictedOutcome = predictedOutcome,
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
