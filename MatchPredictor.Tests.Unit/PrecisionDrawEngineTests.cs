using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class PrecisionDrawEngineTests
{
    [Fact]
    public void DixonColesTauAdjustment_InflatesDrawProbability_ComparedToIndependentPoisson()
    {
        var standardPoissonSettings = Options.Create(new ProbabilityCalculatorSettings
        {
            DixonColesRho = 0.0 // Pure independent Poisson
        });

        var dixonColesSettings = Options.Create(new ProbabilityCalculatorSettings
        {
            DixonColesRho = -0.11 // Dixon-Coles low-score dependency coupling
        });

        var standardCalculator = new ProbabilityCalculator(standardPoissonSettings);
        var coupledCalculator = new ProbabilityCalculator(dixonColesSettings);

        // Low-total, balanced match (typical 1-1 / 0-0 profile)
        var match = new MatchData
        {
            HomeWin = 0.36,
            Draw = 0.30,
            AwayWin = 0.34,
            OverTwoGoals = 0.40,
            UnderTwoGoals = 0.60,
            OverOnePointFive = 0.68,
            UnderOnePointFive = 0.32
        };

        var standardProbs = standardCalculator.CalculateProbabilities(match);
        var coupledProbs = coupledCalculator.CalculateProbabilities(match);

        // Dixon-Coles low-score coupling must boost the draw probability over independent Poisson
        Assert.True(coupledProbs.Draw > standardProbs.Draw,
            $"Coupled draw ({coupledProbs.Draw:P2}) must exceed standard Poisson draw ({standardProbs.Draw:P2})");

        // The probability simplex must still sum cleanly to 1.0
        var total = coupledProbs.HomeWin + coupledProbs.Draw + coupledProbs.AwayWin;
        Assert.InRange(total, 0.999, 1.001);
    }

    [Fact]
    public void SelectPublishedPredictions_RejectsDraw_WhenFixtureIsHighScoring()
    {
        var settings = new PredictionSettings
        {
            DrawStrongThreshold = 0.30,
            MinDrawAffinity = 0.38,
            MaxOver25ProbabilityForDraw = 0.52
        };

        var service = CreateTestAnalyzer(settings);
        var match = new MatchData
        {
            FixtureKey = "high_scoring_draw_test",
            League = "Open League",
            HomeTeam = "Attacking A",
            AwayTeam = "Attacking B",
            MatchLocalDate = new DateOnly(2026, 11, 13),
            MatchLocalTime = new TimeOnly(18, 0),
            HomeWin = 0.36,
            Draw = 0.32,
            AwayWin = 0.32,
            OverTwoGoals = 0.65, // High expected goals
            UnderTwoGoals = 0.35
        };

        var candidates = service.BuildForecastCandidates([match]);
        var published = service.SelectPublishedPredictions(candidates);

        // The draw must be filtered out because Over 2.5 > 0.52
        Assert.DoesNotContain(published, c => c.Market == PredictionMarket.Draw);
    }

    [Fact]
    public void SelectPublishedPredictions_RejectsDraw_WhenTeamsAreLopsided()
    {
        var settings = new PredictionSettings
        {
            DrawStrongThreshold = 0.30,
            MinDrawAffinity = 0.38,
            MaxOver25ProbabilityForDraw = 0.52
        };

        var service = CreateTestAnalyzer(settings);
        var match = new MatchData
        {
            FixtureKey = "lopsided_draw_test",
            League = "Tier 1",
            HomeTeam = "Heavy Favorite",
            AwayTeam = "Underdog",
            MatchLocalDate = new DateOnly(2026, 11, 13),
            MatchLocalTime = new TimeOnly(18, 0),
            HomeWin = 0.72,
            Draw = 0.31,
            AwayWin = 0.10,
            OverTwoGoals = 0.45,
            UnderTwoGoals = 0.55
        };

        var candidates = service.BuildForecastCandidates([match]);
        var published = service.SelectPublishedPredictions(candidates);

        // The draw must be filtered out because parity spread |0.72 - 0.10| = 0.62 drops DAI far below 0.38
        Assert.DoesNotContain(published, c => c.Market == PredictionMarket.Draw);
    }

    [Fact]
    public void SelectPublishedPredictions_PublishesDraw_WhenLowTotalAndTightParity()
    {
        var settings = new PredictionSettings
        {
            DrawStrongThreshold = 0.30,
            MinDrawAffinity = 0.38,
            MaxOver25ProbabilityForDraw = 0.52
        };

        var service = CreateTestAnalyzer(settings);
        var match = new MatchData
        {
            FixtureKey = "sniper_draw_test",
            League = "Tight League",
            HomeTeam = "Defensive A",
            AwayTeam = "Defensive B",
            MatchLocalDate = new DateOnly(2026, 11, 13),
            MatchLocalTime = new TimeOnly(18, 0),
            HomeWin = 0.34,
            Draw = 0.33,
            AwayWin = 0.33,
            OverTwoGoals = 0.38,
            UnderTwoGoals = 0.62 // High Under 2.5 + Tight parity -> High DAI
        };

        var candidates = service.BuildForecastCandidates([match]);
        var published = service.SelectPublishedPredictions(candidates);

        var draw = Assert.Single(published, c => c.Market == PredictionMarket.Draw);
        Assert.True(draw.WasPublished);
        Assert.True(draw.CalibratedProbability >= draw.ThresholdUsed);
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
