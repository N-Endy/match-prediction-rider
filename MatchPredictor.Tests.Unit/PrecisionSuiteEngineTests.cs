using MatchPredictor.Application.Helpers;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class PrecisionSuiteEngineTests
{
    [Fact]
    public void StraightWin_RejectsWin_WhenDrawContaminationIsHigh()
    {
        var settings = new PredictionSettings
        {
            HomeWinStrong = 0.50,
            MinDominanceRatioForWin = 1.25,
            MinWinToDrawRatio = 1.70
        };

        var service = CreateTestAnalyzer(settings);
        // Favorite is 50%, but Draw is 32% and Away is 18% -> DR = 0.50 / 0.50 = 1.0 (< 1.25)
        var match = new MatchData
        {
            FixtureKey = "draw_contamination_test",
            League = "Contaminated League",
            HomeTeam = "Vulnerable Fav",
            AwayTeam = "Gritty Underdog",
            MatchLocalDate = new DateOnly(2026, 11, 15),
            MatchLocalTime = new TimeOnly(18, 0),
            HomeWin = 0.50,
            Draw = 0.32,
            AwayWin = 0.18,
            OverTwoGoals = 0.45,
            UnderTwoGoals = 0.55
        };

        var candidates = service.BuildForecastCandidates([match]);
        var published = service.SelectPublishedPredictions(candidates);

        // Straight Win must be rejected due to low Dominance Ratio (1.0 < 1.25)
        Assert.DoesNotContain(published, c => c.Market is PredictionMarket.HomeWin or PredictionMarket.AwayWin);
    }

    [Fact]
    public void StraightWin_Publishes_WhenDominanceRatioAndWinToDrawRatioAreHigh()
    {
        var settings = new PredictionSettings
        {
            HomeWinStrong = 0.65,
            MinDominanceRatioForWin = 1.25,
            MinWinToDrawRatio = 1.70
        };

        var service = CreateTestAnalyzer(settings);
        // Favorite is 72%, Draw is 18%, Away is 10% -> DR = 0.72 / 0.28 = 2.57, Win/Draw = 4.0
        var match = new MatchData
        {
            FixtureKey = "dominant_win_test",
            League = "Dominant League",
            HomeTeam = "Super Team",
            AwayTeam = "Weak Team",
            MatchLocalDate = new DateOnly(2026, 11, 15),
            MatchLocalTime = new TimeOnly(18, 0),
            HomeWin = 0.72,
            Draw = 0.18,
            AwayWin = 0.10,
            OverTwoGoals = 0.55,
            UnderTwoGoals = 0.45
        };

        var candidates = service.BuildForecastCandidates([match]);
        var published = service.SelectPublishedPredictions(candidates);

        var homeWin = Assert.Single(published, c => c.Market == PredictionMarket.HomeWin);
        Assert.True(homeWin.WasPublished);
    }

    [Fact]
    public void Totals_RejectsOver25_WhenUnder25ExceedsClusterTrapThreshold()
    {
        var settings = new PredictionSettings
        {
            OverTwoGoalsStrongThreshold = 0.52,
            MaxUnder25ProbForOver25 = 0.45
        };

        var service = CreateTestAnalyzer(settings);
        // Over 2.5 is 52%, but Under 2.5 is 48% (> 0.45 cluster trap)
        var match = new MatchData
        {
            FixtureKey = "over25_trap_test",
            League = "Trap League",
            HomeTeam = "Team A",
            AwayTeam = "Team B",
            MatchLocalDate = new DateOnly(2026, 11, 15),
            MatchLocalTime = new TimeOnly(18, 0),
            HomeWin = 0.40,
            Draw = 0.30,
            AwayWin = 0.30,
            OverTwoGoals = 0.52,
            UnderTwoGoals = 0.48
        };

        var candidates = service.BuildForecastCandidates([match]);
        var published = service.SelectPublishedPredictions(candidates);

        // Must reject Over 2.5 due to 2-goal cluster trap (Under 2.5 > 0.45)
        Assert.DoesNotContain(published, c => c.Market == PredictionMarket.Over25Goals);
    }

    [Fact]
    public void Totals_RejectsUnder25_WhenHeavyFavoriteSpikesBlowoutRisk()
    {
        var settings = new PredictionSettings
        {
            UnderTwoGoalsStrongThreshold = 0.55,
            MaxFavoriteWinProbForUnder25 = 0.72
        };

        var service = CreateTestAnalyzer(settings);
        // Under 2.5 is 58%, but Favorite Home Win is 76% (> 0.72 blowout danger)
        var match = new MatchData
        {
            FixtureKey = "blowout_under25_test",
            League = "Mismatched League",
            HomeTeam = "Heavy Giant",
            AwayTeam = "Minnow",
            MatchLocalDate = new DateOnly(2026, 11, 15),
            MatchLocalTime = new TimeOnly(18, 0),
            HomeWin = 0.76,
            Draw = 0.16,
            AwayWin = 0.08,
            OverTwoGoals = 0.42,
            UnderTwoGoals = 0.58
        };

        var candidates = service.BuildForecastCandidates([match]);
        var published = service.SelectPublishedPredictions(candidates);

        // Must reject Under 2.5 because single-team blowout probability destroys the under
        Assert.DoesNotContain(published, c => c.Market == PredictionMarket.Under25Goals);
    }

    [Fact]
    public void Over15Goals_Publishes_WhenGoalExpectancyIsElevated()
    {
        var settings = new PredictionSettings
        {
            OverOneFiveStrongThreshold = 0.74,
            EnableOver15Predictions = true
        };

        var service = CreateTestAnalyzer(settings);
        var match = new MatchData
        {
            FixtureKey = "over15_valid_test",
            League = "High Scoring League",
            HomeTeam = "Offensive Home",
            AwayTeam = "Offensive Away",
            MatchLocalDate = new DateOnly(2026, 11, 15),
            MatchLocalTime = new TimeOnly(18, 0),
            HomeWin = 0.45,
            Draw = 0.25,
            AwayWin = 0.30,
            OverOnePointFive = 0.82,
            OverTwoGoals = 0.58,
            UnderTwoGoals = 0.42
        };

        var candidates = service.BuildForecastCandidates([match]);
        var published = service.SelectPublishedPredictions(candidates);

        var over15 = Assert.Single(published, c => c.Market == PredictionMarket.Over15Goals);
        Assert.True(over15.WasPublished);
        Assert.Equal("Over 1.5", over15.PredictedOutcome);
    }

    [Fact]
    public void DoubleChance_Publishes1X_WhenUnderdogHasMinimalWinProbability()
    {
        var settings = new PredictionSettings
        {
            DoubleChanceStrongThreshold = 0.74,
            MaxUnderdogProbabilityForDoubleChance = 0.22,
            EnableDoubleChancePredictions = true
        };

        var service = CreateTestAnalyzer(settings);
        // Home 58%, Draw 26%, Away 16% -> 1X = 84% >= 74%, and Underdog Away (16%) <= 22%
        var match = new MatchData
        {
            FixtureKey = "dc_valid_test",
            League = "Solid League",
            HomeTeam = "Strong Host",
            AwayTeam = "Weak Visitor",
            MatchLocalDate = new DateOnly(2026, 11, 15),
            MatchLocalTime = new TimeOnly(18, 0),
            HomeWin = 0.58,
            Draw = 0.26,
            AwayWin = 0.16,
            OverTwoGoals = 0.48,
            UnderTwoGoals = 0.52
        };

        var candidates = service.BuildForecastCandidates([match]);
        var published = service.SelectPublishedPredictions(candidates);

        var dc = Assert.Single(published, c => c.Market == PredictionMarket.DoubleChance);
        Assert.True(dc.WasPublished);
        Assert.Equal("1X", dc.PredictedOutcome);
    }

    [Fact]
    public void DoubleChance_Rejects1X_WhenUnderdogWinProbabilityIsTooHigh()
    {
        var settings = new PredictionSettings
        {
            DoubleChanceStrongThreshold = 0.74,
            MaxUnderdogProbabilityForDoubleChance = 0.22,
            EnableDoubleChancePredictions = true
        };

        var service = CreateTestAnalyzer(settings);
        // Home 46%, Draw 28%, Away 26% -> 1X = 74%, but Away (26%) > 22%
        var match = new MatchData
        {
            FixtureKey = "dc_too_risky_test",
            League = "Balanced League",
            HomeTeam = "Fav A",
            AwayTeam = "Live Dog B",
            MatchLocalDate = new DateOnly(2026, 11, 15),
            MatchLocalTime = new TimeOnly(18, 0),
            HomeWin = 0.46,
            Draw = 0.28,
            AwayWin = 0.26,
            OverTwoGoals = 0.48,
            UnderTwoGoals = 0.52
        };

        var candidates = service.BuildForecastCandidates([match]);
        var published = service.SelectPublishedPredictions(candidates);

        Assert.DoesNotContain(published, c => c.Market == PredictionMarket.DoubleChance);
    }

    [Theory]
    [InlineData("Over1.5Goals", "Over 1.5", "2 - 0", true)]
    [InlineData("Over1.5Goals", "Over 1.5", "1 - 1", true)]
    [InlineData("Over1.5Goals", "Over 1.5", "1 - 0", false)]
    [InlineData("Over1.5Goals", "Over 1.5", "0 - 0", false)]
    [InlineData("DoubleChance", "1X", "2 - 1", true)]
    [InlineData("DoubleChance", "1X", "1 - 1", true)]
    [InlineData("DoubleChance", "1X", "0 - 1", false)]
    [InlineData("DoubleChance", "X2", "0 - 2", true)]
    [InlineData("DoubleChance", "X2", "1 - 1", true)]
    [InlineData("DoubleChance", "X2", "2 - 0", false)]
    public void PredictionScoreClassHelper_CorrectlySettles_Over15AndDoubleChance(
        string category, string outcome, string actualScore, bool expectedCorrect)
    {
        var prediction = new Prediction
        {
            PredictionCategory = category,
            PredictedOutcome = outcome,
            ActualScore = actualScore,
            IsLive = false
        };

        var isCorrect = PredictionScoreClassHelper.IsPredictionCorrect(prediction);
        Assert.Equal(expectedCorrect, isCorrect);
    }

    private static DataAnalyzerService CreateTestAnalyzer(PredictionSettings settings)
    {
        var calculator = new ProbabilityCalculator();
        return new DataAnalyzerService(
            calculator,
            new PassThroughCalibrationService(),
            new PassThroughThresholdService(),
            new PassThroughCorrectionService(),
            Options.Create(settings));
    }

    private sealed class PassThroughCalibrationService : ICalibrationService
    {
        public double Calibrate(PredictionMarket market, double uncalibratedProbability, string? league = null) =>
            uncalibratedProbability;

        public CalibrationDecision CalibrateWithDecision(PredictionMarket market, double uncalibratedProbability, string? league = null) =>
            new() { Probability = uncalibratedProbability, CalibratorUsed = "PassThrough" };

        public Task RebuildProfilesAsync() => Task.CompletedTask;
    }

    private sealed class PassThroughThresholdService : IThresholdTuningService
    {
        public double GetThreshold(PredictionMarket market, double fallbackThreshold, string? league = null) =>
            fallbackThreshold;

        public ThresholdDecision GetThresholdDecision(PredictionMarket market, double fallbackThreshold, string? league = null) =>
            new() { Threshold = fallbackThreshold, ThresholdSource = "Configured" };

        public Task RebuildProfilesAsync() => Task.CompletedTask;
    }

    private sealed class PassThroughCorrectionService : IProbabilityCorrectionService
    {
        public double ApplyCorrection(PredictionMarket market, double rawProbability) => rawProbability;

        public Task RebuildProfilesAsync() => Task.CompletedTask;
    }
}
