using MatchPredictor.Application.Helpers;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class PrecisionBttsEngineTests
{
    [Fact]
    public void ProbabilityCalculator_WeakerTeamScoringFloor_DampensBtts_InLopsidedGame()
    {
        var calculator = new ProbabilityCalculator();

        // Match A: Balanced fixture (1.30 vs 1.30 xG expectancy)
        var balancedMatch = new MatchData
        {
            HomeWin = 0.38,
            Draw = 0.28,
            AwayWin = 0.34,
            OverTwoGoals = 0.55,
            UnderTwoGoals = 0.45,
            OverOnePointFive = 0.80,
            UnderOnePointFive = 0.20,
            BttsYes = 0.60,
            BttsNo = 0.40
        };

        // Match B: Lopsided fixture with same total goal expectancy (2.20 vs 0.40 xG expectancy)
        var lopsidedMatch = new MatchData
        {
            HomeWin = 0.78,
            Draw = 0.14,
            AwayWin = 0.08,
            OverTwoGoals = 0.55,
            UnderTwoGoals = 0.45,
            OverOnePointFive = 0.80,
            UnderOnePointFive = 0.20,
            BttsYes = 0.60,
            BttsNo = 0.40
        };

        var balancedProbs = calculator.CalculateProbabilities(balancedMatch);
        var lopsidedProbs = calculator.CalculateProbabilities(lopsidedMatch);

        // In the lopsided game, the underdog scoring floor dampens BTTS because of high clean sheet risk
        Assert.True(balancedProbs.Btts > lopsidedProbs.Btts,
            $"Balanced BTTS ({balancedProbs.Btts:P2}) must exceed lopsided BTTS ({lopsidedProbs.Btts:P2})");
    }

    [Fact]
    public void ProbabilityCalculator_PhysicalCeiling_ClampsBttsBelowOver15()
    {
        var calculator = new ProbabilityCalculator();

        var match = new MatchData
        {
            HomeWin = 0.40,
            Draw = 0.25,
            AwayWin = 0.35,
            OverTwoGoals = 0.50,
            UnderTwoGoals = 0.50,
            OverOnePointFive = 0.65,
            UnderOnePointFive = 0.35,
            BttsYes = 0.70, // Artificially inflated
            BttsNo = 0.30
        };

        var probs = calculator.CalculateProbabilities(match);

        // BTTS cannot mathematically exceed Over 1.5; model enforces a 0.92 upper ceiling bound
        Assert.True(probs.Btts <= 0.65 * 0.92 + 0.01,
            $"Calculated BTTS ({probs.Btts:P2}) must respect Over 1.5 ceiling ({0.65 * 0.92:P2})");
    }

    [Fact]
    public void SelectPublishedPredictions_RejectsBttsYes_AndPublishesBttsNo_WhenTeamsAreLopsided()
    {
        var settings = new PredictionSettings
        {
            BttsScoreThreshold = 0.50,
            MinBttsAffinity = 0.46,
            MaxSpreadForBttsYes = 0.45,
            EnableBttsNoPredictions = true
        };

        var service = CreateTestAnalyzer(settings);
        var match = new MatchData
        {
            FixtureKey = "lopsided_btts_test",
            League = "Tier 1",
            HomeTeam = "Powerhouse FC",
            AwayTeam = "Minnows United",
            MatchLocalDate = new DateOnly(2026, 11, 13),
            MatchLocalTime = new TimeOnly(18, 0),
            HomeWin = 0.78,
            Draw = 0.14,
            AwayWin = 0.08,
            OverTwoGoals = 0.55,
            UnderTwoGoals = 0.45,
            BttsYes = 0.42,
            BttsNo = 0.58
        };
        match.NormalizeSourceProbabilities();

        var candidates = service.BuildForecastCandidates([match]);
        var published = service.SelectPublishedPredictions(candidates);

        // BTTS Yes must be rejected due to parity spread |0.78 - 0.08| = 0.70 > 0.45
        Assert.DoesNotContain(published, c => c.Market == PredictionMarket.BothTeamsScore && c.PredictedOutcome == "BTTS");

        // BTTS No must be published because lopsided clean-sheet dynamics dominate
        var bttsNo = Assert.Single(published, c => c.Market == PredictionMarket.BothTeamsScore && c.PredictedOutcome == "No BTTS");
        Assert.True(bttsNo.WasPublished);
        Assert.True(bttsNo.CalibratedProbability >= bttsNo.ThresholdUsed);
    }

    [Fact]
    public void SelectPublishedPredictions_RejectsBttsYes_AndPublishesBttsNo_WhenDefensiveLowTotal()
    {
        var settings = new PredictionSettings
        {
            BttsScoreThreshold = 0.50,
            MinBttsAffinity = 0.46,
            MaxSpreadForBttsYes = 0.45,
            EnableBttsNoPredictions = true
        };

        var service = CreateTestAnalyzer(settings);
        var match = new MatchData
        {
            FixtureKey = "defensive_btts_test",
            League = "Tight League",
            HomeTeam = "Iron Defense",
            AwayTeam = "Lockdown FC",
            MatchLocalDate = new DateOnly(2026, 11, 13),
            MatchLocalTime = new TimeOnly(18, 0),
            HomeWin = 0.35,
            Draw = 0.33,
            AwayWin = 0.32,
            OverTwoGoals = 0.35,
            UnderTwoGoals = 0.65, // Defensive match: Under 2.5 > 0.52
            BttsYes = 0.40,
            BttsNo = 0.60
        };
        match.NormalizeSourceProbabilities();

        var candidates = service.BuildForecastCandidates([match]);
        var published = service.SelectPublishedPredictions(candidates);

        // BTTS Yes must be rejected because Under 2.5 > 0.52
        Assert.DoesNotContain(published, c => c.Market == PredictionMarket.BothTeamsScore && c.PredictedOutcome == "BTTS");

        // BTTS No must be published
        var bttsNo = Assert.Single(published, c => c.Market == PredictionMarket.BothTeamsScore && c.PredictedOutcome == "No BTTS");
        Assert.True(bttsNo.WasPublished);
    }

    [Fact]
    public void SelectPublishedPredictions_PublishesBttsYes_WhenHighTotalAndBalanced()
    {
        var settings = new PredictionSettings
        {
            BttsScoreThreshold = 0.55,
            MinBttsAffinity = 0.46,
            MaxSpreadForBttsYes = 0.45,
            EnableBttsNoPredictions = true
        };

        var service = CreateTestAnalyzer(settings);
        var match = new MatchData
        {
            FixtureKey = "thriller_btts_test",
            League = "Attacking League",
            HomeTeam = "Arsenal Style",
            AwayTeam = "Liverpool Style",
            MatchLocalDate = new DateOnly(2026, 11, 13),
            MatchLocalTime = new TimeOnly(18, 0),
            HomeWin = 0.42,
            Draw = 0.22,
            AwayWin = 0.36, // Parity spread = 0.06 (tight)
            OverTwoGoals = 0.68,
            UnderTwoGoals = 0.32, // High scoring
            BttsYes = 0.66,
            BttsNo = 0.34
        };
        match.NormalizeSourceProbabilities();

        var candidates = service.BuildForecastCandidates([match]);
        var published = service.SelectPublishedPredictions(candidates);

        // BTTS Yes qualifies and is published as the winning pick for this fixture
        var btts = Assert.Single(published, c => c.Market == PredictionMarket.BothTeamsScore);
        Assert.Equal("BTTS", btts.PredictedOutcome);
        Assert.True(btts.WasPublished);
        Assert.True(btts.CalibratedProbability >= btts.ThresholdUsed);
    }

    [Fact]
    public void MarketQuoteResolver_ResolvesBttsNoOddsAndProbability()
    {
        var match = new MatchData
        {
            BttsYes = 0.38,
            BttsNo = 0.62
        };

        var sourceFixture = new SourceMarketFixture
        {
            BttsYesOdds = 2.45,
            BttsNoOdds = 1.62,
            BttsYesProbability = 0.38,
            BttsNoProbability = 0.62
        };

        var prediction = new Prediction
        {
            PredictionCategory = "BothTeamsScore",
            PredictedOutcome = "No BTTS"
        };

        var resolved = MarketQuoteResolver.TryResolve(match, prediction, sourceFixture, out var quote);

        Assert.True(resolved);
        // Raw odds 1.62 is de-vigged against pair (2.45, 1.62) -> fair probability ~ 0.606
        Assert.Equal(0.606, quote.MarketProbability, 2);
        Assert.Equal(1.62, quote.DecimalOdds, 2);

        // Without live raw odds, falls back cleanly to stored normalized probability
        var resolvedStored = MarketQuoteResolver.TryResolve(match, prediction, sourceFixture: null, out var storedQuote);
        Assert.True(resolvedStored);
        Assert.Equal(0.62, storedQuote.MarketProbability, 2);
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
