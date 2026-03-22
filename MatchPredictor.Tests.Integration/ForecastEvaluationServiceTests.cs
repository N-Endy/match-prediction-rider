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
        Assert.Equal(1, categoryStats.Total);
        Assert.Equal(1, categoryStats.Correct);
        Assert.Equal(1.0, categoryStats.Accuracy, 5);
        Assert.Equal(1, stats.SettledForecasts);
        Assert.Equal(PredictionMarket.Under25Goals, marketStats.Market);
    }

    [Fact]
    public void CalculateStats_CountsSettledOverUnderLoss_WhenActualOutcomeExistsButActualScoreIsNotParsable()
    {
        var service = new ForecastEvaluationService();
        var kickoff = DateTime.UtcNow.AddHours(-8);
        var localDate = DateOnly.FromDateTime(kickoff);

        var predictions = new[]
        {
            new Prediction
            {
                MatchLocalDate = localDate,
                MatchLocalTime = TimeOnly.FromDateTime(kickoff),
                MatchDateTime = kickoff,
                FixtureKey = "league|over-under-home|over-under-away",
                League = "League",
                HomeTeam = "Over Under Home",
                AwayTeam = "Over Under Away",
                PredictionCategory = "OverUnderSets",
                PredictedOutcome = "Over 2.5 Sets",
                ActualScore = "RET",
                ActualOutcome = "Under 2.5 Sets",
                IsLive = false,
                ConfidenceScore = 0.68m,
                CreatedAt = kickoff.AddHours(-2)
            }
        };

        var stats = service.CalculateStats(predictions, Array.Empty<ForecastObservation>());
        var categoryStats = Assert.Single(stats.CategoryStats.Values);

        Assert.Equal(1, stats.TotalPredictions);
        Assert.Equal(1, stats.CompletedPredictions);
        Assert.Equal(0, stats.CorrectPredictions);
        Assert.Equal("OverUnderSets", categoryStats.Category);
        Assert.Equal(1, categoryStats.Total);
        Assert.Equal(0, categoryStats.Correct);
        Assert.Equal(0.0, categoryStats.Accuracy, 5);
    }

    [Fact]
    public void CalculateCurrentRevisionStats_UsesLatestVisibleRevisionInsteadOfPointInTimeBacktestSnapshot()
    {
        var service = new ForecastEvaluationService();
        var kickoff = DateTime.UtcNow.AddHours(-8);
        var localDate = DateOnly.FromDateTime(kickoff);

        var predictions = new[]
        {
            new Prediction
            {
                MatchLocalDate = localDate,
                MatchLocalTime = TimeOnly.FromDateTime(kickoff),
                MatchDateTime = kickoff,
                FixtureKey = "league|fixture|one",
                League = "League",
                HomeTeam = "Alpha",
                AwayTeam = "Beta",
                PredictionCategory = "OverUnderSets",
                PredictedOutcome = "Under 2.5 Sets",
                ActualScore = "2:1",
                ActualOutcome = "Over 2.5 Sets",
                IsLive = false,
                IsCurrentRevision = false,
                RevisionNumber = 1,
                ConfidenceScore = 0.61m,
                CreatedAt = kickoff.AddHours(-2)
            },
            new Prediction
            {
                MatchLocalDate = localDate,
                MatchLocalTime = TimeOnly.FromDateTime(kickoff),
                MatchDateTime = kickoff,
                FixtureKey = "league|fixture|one",
                League = "League",
                HomeTeam = "Alpha",
                AwayTeam = "Beta",
                PredictionCategory = "OverUnderSets",
                PredictedOutcome = "Over 2.5 Sets",
                ActualScore = "2:1",
                ActualOutcome = "Over 2.5 Sets",
                IsLive = false,
                IsCurrentRevision = true,
                RevisionNumber = 2,
                ConfidenceScore = 0.67m,
                CreatedAt = kickoff.AddHours(1)
            }
        };

        var backtestStats = service.CalculateStats(predictions, Array.Empty<ForecastObservation>());
        var currentRevisionStats = service.CalculateCurrentRevisionStats(predictions);

        Assert.Equal(1, backtestStats.CompletedPredictions);
        Assert.Equal(0, backtestStats.CorrectPredictions);
        Assert.Equal(1, currentRevisionStats.CompletedPredictions);
        Assert.Equal(1, currentRevisionStats.CorrectPredictions);
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
