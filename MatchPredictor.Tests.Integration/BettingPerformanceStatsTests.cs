using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Services;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class BettingPerformanceStatsTests
{
    [Fact]
    public void CalculateStats_ComputesRealizedRoiWinRateAndPerMarketRollups()
    {
        var service = new ForecastEvaluationService();
        var kickoff = DateTime.UtcNow.AddHours(-6);
        var localDate = DateOnly.FromDateTime(kickoff);

        var predictions = new[]
        {
            BuildPrediction(1, "fixture-a", "BothTeamsScore", "BTTS", "1:1", 0.60m, kickoff, localDate),
            BuildPrediction(2, "fixture-b", "BothTeamsScore", "BTTS", "1:0", 0.55m, kickoff, localDate),
            BuildPrediction(3, "fixture-c", "Over2.5Goals", "Over 2.5", "2:1", 0.65m, kickoff, localDate),
            BuildPrediction(4, "fixture-d", "Over2.5Goals", "Over 2.5", "0:0", 0.60m, kickoff, localDate)
        };

        var snapshots = new[]
        {
            BuildSnapshot(1, PredictionOddsSnapshotKind.Publish, 2.0, "BTTS"),
            BuildSnapshot(1, PredictionOddsSnapshotKind.Close, 1.8, "BTTS"),
            BuildSnapshot(2, PredictionOddsSnapshotKind.Publish, 2.0, "BTTS"),
            BuildSnapshot(2, PredictionOddsSnapshotKind.Close, 2.2, "BTTS"),
            BuildSnapshot(3, PredictionOddsSnapshotKind.Publish, 1.8, "Over 2.5"),
            BuildSnapshot(4, PredictionOddsSnapshotKind.Publish, 2.5, "Over 2.5")
        };

        var stats = service.CalculateStats(predictions, Array.Empty<ForecastObservation>(), snapshots);
        var betting = stats.BettingPerformance;

        Assert.Equal(4, betting.SettledBetCount);
        Assert.Equal(2, betting.WinningBetCount);
        Assert.Equal(0.5, betting.WinRate, 6);
        Assert.Equal(4.0, betting.TotalStakedUnits, 6);
        // +1.0 (A) -1.0 (B) +0.8 (C) -1.0 (D)
        Assert.Equal(-0.2, betting.NetProfitUnits, 6);
        Assert.Equal(-0.05, betting.RoiPercent, 6);
        Assert.Equal(betting.RoiPercent, betting.YieldPercent, 6);
        Assert.True(betting.MaxDrawdownUnits > 0);

        // CLV is only available for the two BothTeamsScore bets that captured a closing price.
        Assert.Equal(2, betting.ClosingLineSamples);
        Assert.Equal(0.5, betting.BeatCloseRate, 6);

        Assert.Equal(2, betting.Markets.Count);
        var bttsMarket = Assert.Single(betting.Markets, market => market.MarketKey == "BothTeamsScore");
        Assert.Equal(2, bttsMarket.SettledBetCount);
        Assert.Equal(0.0, bttsMarket.NetProfitUnits, 6);
        Assert.Equal(0.0, bttsMarket.RoiPercent, 6);
        Assert.Equal(2, bttsMarket.ClosingLineSamples);

        var overMarket = Assert.Single(betting.Markets, market => market.MarketKey == "Over2.5Goals");
        Assert.Equal(2, overMarket.SettledBetCount);
        Assert.Equal(-0.2, overMarket.NetProfitUnits, 6);
        Assert.Equal(0, overMarket.ClosingLineSamples);
    }

    [Fact]
    public void CalculateStats_AppliesFractionalKellyStakingOnlyToPositiveEdges()
    {
        var service = new ForecastEvaluationService();
        var kickoff = DateTime.UtcNow.AddHours(-6);
        var localDate = DateOnly.FromDateTime(kickoff);

        var predictions = new[]
        {
            // Positive edge: p=0.60, odds 2.0 -> raw Kelly 0.2, quarter-Kelly stake 0.05.
            BuildPrediction(1, "fixture-a", "BothTeamsScore", "BTTS", "1:1", 0.60m, kickoff, localDate),
            // No edge: p=0.40, odds 2.0 -> raw Kelly <= 0, excluded from Kelly staking.
            BuildPrediction(2, "fixture-b", "BothTeamsScore", "BTTS", "1:0", 0.40m, kickoff, localDate)
        };

        var snapshots = new[]
        {
            BuildSnapshot(1, PredictionOddsSnapshotKind.Publish, 2.0, "BTTS"),
            BuildSnapshot(2, PredictionOddsSnapshotKind.Publish, 2.0, "BTTS")
        };

        var betting = service.CalculateStats(predictions, Array.Empty<ForecastObservation>(), snapshots).BettingPerformance;

        Assert.Equal(0.25, betting.KellyFraction, 6);
        Assert.Equal(1, betting.KellyBetCount);
        Assert.Equal(0.05, betting.KellyStakedUnits, 6);
        // The single Kelly bet won at odds 2.0: profit = 0.05 * (2.0 - 1.0) = 0.05.
        Assert.Equal(0.05, betting.KellyNetProfitUnits, 6);
        Assert.Equal(1.0, betting.KellyRoiPercent, 6);
    }

    [Fact]
    public void CalculateStats_WithoutSnapshots_ReturnsEmptyBettingPerformance()
    {
        var service = new ForecastEvaluationService();
        var kickoff = DateTime.UtcNow.AddHours(-6);
        var localDate = DateOnly.FromDateTime(kickoff);

        var predictions = new[]
        {
            BuildPrediction(1, "fixture-a", "BothTeamsScore", "BTTS", "1:1", 0.60m, kickoff, localDate)
        };

        var betting = service.CalculateStats(predictions, Array.Empty<ForecastObservation>()).BettingPerformance;

        Assert.Equal(0, betting.SettledBetCount);
        Assert.Empty(betting.Markets);
    }

    [Fact]
    public void CalculateStats_ComputesMaxDrawdownInKickoffOrder()
    {
        var service = new ForecastEvaluationService();
        var lateKickoff = DateTime.UtcNow.AddHours(-2);
        var midKickoff = DateTime.UtcNow.AddHours(-4);
        var earlyKickoff = DateTime.UtcNow.AddHours(-6);
        var localDate = DateOnly.FromDateTime(earlyKickoff);

        // Enumeration order is lose, win, lose (drawdown 1u). Kickoff order is win, lose, lose (drawdown 2u).
        var predictions = new[]
        {
            BuildPrediction(1, "fixture-late", "BothTeamsScore", "BTTS", "1:0", 0.60m, lateKickoff, localDate),
            BuildPrediction(2, "fixture-early", "BothTeamsScore", "BTTS", "1:1", 0.60m, earlyKickoff, localDate),
            BuildPrediction(3, "fixture-mid", "BothTeamsScore", "BTTS", "1:0", 0.60m, midKickoff, localDate)
        };

        var snapshots = new[]
        {
            BuildSnapshot(1, PredictionOddsSnapshotKind.Publish, 2.0, "BTTS"),
            BuildSnapshot(2, PredictionOddsSnapshotKind.Publish, 2.0, "BTTS"),
            BuildSnapshot(3, PredictionOddsSnapshotKind.Publish, 2.0, "BTTS")
        };

        var betting = service.CalculateStats(predictions, Array.Empty<ForecastObservation>(), snapshots).BettingPerformance;

        Assert.Equal(3, betting.SettledBetCount);
        Assert.Equal(2.0, betting.MaxDrawdownUnits, 6);
    }

    [Fact]
    public void CalculateStats_AttachesCloseOddsFromSiblingRevision_ForClv()
    {
        var service = new ForecastEvaluationService();
        var kickoff = DateTime.UtcNow.AddHours(-6);
        var localDate = DateOnly.FromDateTime(kickoff);
        const string fixtureKey = "fixture-revisioned";

        var pitRevision = BuildPrediction(1, fixtureKey, "BothTeamsScore", "BTTS", "1:1", 0.60m, kickoff, localDate);
        pitRevision.RevisionNumber = 1;
        pitRevision.CreatedAt = kickoff.AddHours(-2);

        var currentRevision = BuildPrediction(2, fixtureKey, "BothTeamsScore", "BTTS", "1:1", 0.90m, kickoff, localDate);
        currentRevision.RevisionNumber = 2;
        currentRevision.CreatedAt = kickoff.AddHours(1);
        currentRevision.IsCurrentRevision = true;
        pitRevision.IsCurrentRevision = false;

        var snapshots = new[]
        {
            BuildSnapshot(1, PredictionOddsSnapshotKind.Publish, 2.0, "BTTS"),
            BuildSnapshot(2, PredictionOddsSnapshotKind.Close, 1.8, "BTTS")
        };

        var betting = service.CalculateStats(
            [pitRevision, currentRevision],
            Array.Empty<ForecastObservation>(),
            snapshots).BettingPerformance;

        Assert.Equal(1, betting.SettledBetCount);
        Assert.Equal(1, betting.ClosingLineSamples);
        Assert.Equal(Math.Round((2.0 / 1.8) - 1.0, 6), betting.AverageClosingLineValuePercent, 6);
    }

    private static Prediction BuildPrediction(
        int id,
        string fixtureKey,
        string category,
        string predictedOutcome,
        string actualScore,
        decimal confidence,
        DateTime kickoff,
        DateOnly localDate)
    {
        return new Prediction
        {
            Id = id,
            Date = kickoff.ToString("dd-MM-yyyy"),
            Time = "18:00",
            MatchLocalDate = localDate,
            MatchLocalTime = TimeOnly.FromDateTime(kickoff),
            MatchDateTime = kickoff,
            FixtureKey = fixtureKey,
            League = "League",
            HomeTeam = $"Home {id}",
            AwayTeam = $"Away {id}",
            PredictionCategory = category,
            PredictedOutcome = predictedOutcome,
            ActualScore = actualScore,
            IsLive = false,
            WasPublished = true,
            IsCurrentRevision = true,
            ConfidenceScore = confidence,
            CreatedAt = kickoff.AddHours(-2)
        };
    }

    private static PredictionOddsSnapshot BuildSnapshot(
        int predictionId,
        PredictionOddsSnapshotKind kind,
        double decimalOdds,
        string outcome)
    {
        return new PredictionOddsSnapshot
        {
            PredictionId = predictionId,
            SnapshotKind = kind,
            DecimalOdds = decimalOdds,
            Outcome = outcome,
            Market = "Market",
            ImpliedProbability = 1.0 / decimalOdds,
            CapturedAtUtc = DateTime.UtcNow
        };
    }
}
