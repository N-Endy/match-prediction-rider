using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Services;
using MatchPredictor.Infrastructure.Statistics;
using Microsoft.Extensions.Options;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class DataAnalyzerStatisticalBlendTests
{
    [Fact]
    public void BuildForecastCandidates_BlendsStatisticalSignalIntoRawProbability()
    {
        var match = new MatchData
        {
            Date = "12-03-2026",
            Time = "18:00",
            League = "Test",
            HomeTeam = "Alpha",
            AwayTeam = "Beta"
        };

        var marketCalculator = new FixedProbabilityCalculator(new MatchProbabilities(
            Btts: 0.50, Over25: 0.55, Under25: 0.45, Draw: 0.25, HomeWin: 0.50, AwayWin: 0.25));

        var statisticalSignal = new MatchProbabilities(
            Btts: 0.60, Over25: 0.65, Under25: 0.35, Draw: 0.30, HomeWin: 0.45, AwayWin: 0.25);

        var service = new DataAnalyzerService(
            marketCalculator,
            new IdentityCalibrationService(),
            new ConfiguredThresholdService(),
            new IdentityCorrectionService(),
            Options.Create(new PredictionSettings()),
            new FixedSignalProvider(statisticalSignal));

        var candidates = service.BuildForecastCandidates([match]);

        var over = candidates.Single(candidate => candidate.Market == PredictionMarket.Over25Goals);
        var btts = candidates.Single(candidate => candidate.Market == PredictionMarket.BothTeamsScore);

        // Market weight 1.0, Dixon-Coles weight 1.1, blended in logit space.
        var expectedOver = EnsembleProbabilityBlender.BlendLogit((0.55, 1.0), (0.65, 1.1));
        var expectedBtts = EnsembleProbabilityBlender.BlendLogit((0.50, 1.0), (0.60, 1.1));

        Assert.Equal(expectedOver, over.RawProbability, 4);
        Assert.Equal(expectedBtts, btts.RawProbability, 4);
        // The blended value must sit between the market and statistical inputs.
        Assert.InRange(over.RawProbability, 0.55, 0.65);
    }

    [Fact]
    public void BuildForecastCandidates_WithoutProvider_UsesPureMarketProbability()
    {
        var match = new MatchData
        {
            Date = "12-03-2026",
            Time = "18:00",
            League = "Test",
            HomeTeam = "Alpha",
            AwayTeam = "Beta"
        };

        var marketCalculator = new FixedProbabilityCalculator(new MatchProbabilities(
            Btts: 0.50, Over25: 0.55, Under25: 0.45, Draw: 0.25, HomeWin: 0.50, AwayWin: 0.25));

        var service = new DataAnalyzerService(
            marketCalculator,
            new IdentityCalibrationService(),
            new ConfiguredThresholdService(),
            new IdentityCorrectionService(),
            Options.Create(new PredictionSettings()));

        var candidates = service.BuildForecastCandidates([match]);
        var over = candidates.Single(candidate => candidate.Market == PredictionMarket.Over25Goals);

        Assert.Equal(0.55, over.RawProbability, 6);
    }

    private sealed class FixedProbabilityCalculator(MatchProbabilities probabilities) : IProbabilityCalculator
    {
        public MatchProbabilities CalculateProbabilities(MatchData match) => probabilities;
        public double CalculateBttsProbability(MatchData match) => probabilities.Btts;
        public double CalculateOverTwoGoalsProbability(MatchData match) => probabilities.Over25;
        public double CalculateUnderTwoGoalsProbability(MatchData match) => probabilities.Under25;
        public double CalculateHomeWinProbability(MatchData match) => probabilities.HomeWin;
        public double CalculateAwayWinProbability(MatchData match) => probabilities.AwayWin;
    }

    private sealed class FixedSignalProvider(MatchProbabilities signal) : IStatisticalSignalProvider
    {
        public IStatisticalSignalSet BuildSignals(IReadOnlyCollection<MatchData> matches) => new Set(signal);

        private sealed class Set(MatchProbabilities signal) : IStatisticalSignalSet
        {
            public MatchProbabilities? GetSignal(MatchData match) => signal;
        }
    }

    private sealed class IdentityCalibrationService : ICalibrationService
    {
        public double Calibrate(PredictionMarket market, double rawProbability, string? league = null) => rawProbability;

        public CalibrationDecision CalibrateWithDecision(PredictionMarket market, double rawProbability, string? league = null) =>
            new() { Probability = rawProbability, CalibratorUsed = "Bucket" };

        public Task RebuildProfilesAsync() => Task.CompletedTask;
    }

    private sealed class ConfiguredThresholdService : IThresholdTuningService
    {
        public double GetThreshold(PredictionMarket market, double fallbackThreshold, string? league = null) => fallbackThreshold;

        public ThresholdDecision GetThresholdDecision(PredictionMarket market, double fallbackThreshold, string? league = null) =>
            new() { Threshold = fallbackThreshold, ThresholdSource = "Configured" };

        public Task RebuildProfilesAsync() => Task.CompletedTask;
    }

    private sealed class IdentityCorrectionService : IProbabilityCorrectionService
    {
        public double ApplyCorrection(PredictionMarket market, double rawProbability) => rawProbability;
        public Task RebuildProfilesAsync() => Task.CompletedTask;
    }
}
