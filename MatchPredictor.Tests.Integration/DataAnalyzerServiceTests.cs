using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class DataAnalyzerServiceTests
{
    [Fact]
    public void StraightWin_UsesHigherCalibratedSideAndRespectsConfiguredThreshold()
    {
        var match = CreateMatch();
        var service = new DataAnalyzerService(
            new FakeProbabilityCalculator
            {
                HomeWin = 0.66,
                AwayWin = 0.62
            },
            new FakeCalibrationService((market, raw) =>
            {
                return (market, raw) switch
                {
                    (PredictionMarket.HomeWin, 0.66) => 0.69,
                    (PredictionMarket.AwayWin, 0.62) => 0.72,
                    _ => raw
                };
            }),
            new FakeThresholdTuningService(),
            Options.Create(new PredictionSettings
            {
                HomeWinStrong = 0.68,
                AwayWinStrong = 0.70
            }));

        var candidates = service.StraightWin([match]);
        var candidate = Assert.Single(candidates);

        Assert.Equal("StraightWin", candidate.PredictionCategory);
        Assert.Equal("Away Win", candidate.PredictedOutcome);
        Assert.Equal(0.62, candidate.RawProbability, 3);
        Assert.Equal(0.72, candidate.CalibratedProbability, 3);
        Assert.Equal("Bucket", candidate.CalibratorUsed);
        Assert.Equal(0.70, candidate.ThresholdUsed, 3);
        Assert.Equal("Configured", candidate.ThresholdSource);
        Assert.True(candidate.WasPublished);
    }

    [Fact]
    public void OverTwoGoals_CarriesRawAndCalibratedProbability()
    {
        var match = CreateMatch();
        var service = new DataAnalyzerService(
            new FakeProbabilityCalculator
            {
                Over25 = 0.57
            },
            new FakeCalibrationService((market, raw) =>
                market == PredictionMarket.Over25Goals ? 0.60 : raw),
            new FakeThresholdTuningService(),
            Options.Create(new PredictionSettings
            {
                OverTwoGoalsStrongThreshold = 0.58
            }));

        var candidates = service.OverTwoGoals([match]);
        var candidate = Assert.Single(candidates);

        Assert.Equal("Over2.5Goals", candidate.PredictionCategory);
        Assert.Equal("Over 2.5", candidate.PredictedOutcome);
        Assert.Equal(0.57, candidate.RawProbability, 3);
        Assert.Equal(0.60, candidate.CalibratedProbability, 3);
        Assert.Equal("Bucket", candidate.CalibratorUsed);
        Assert.Equal(0.58, candidate.ThresholdUsed, 3);
        Assert.Equal("Configured", candidate.ThresholdSource);
        Assert.True(candidate.WasPublished);
    }

    [Fact]
    public void OverTwoGoals_PreservesExistingNormalizedWatTime()
    {
        var match = CreateMatch();
        match.Time = "19:00";
        match.MatchDateTime = new DateTime(2026, 3, 12, 18, 0, 0, DateTimeKind.Utc);

        var service = new DataAnalyzerService(
            new FakeProbabilityCalculator
            {
                Over25 = 0.61
            },
            new FakeCalibrationService((_, raw) => raw),
            new FakeThresholdTuningService(),
            Options.Create(new PredictionSettings
            {
                OverTwoGoalsStrongThreshold = 0.58
            }));

        var candidates = service.OverTwoGoals([match]);
        var candidate = Assert.Single(candidates);

        Assert.Equal("19:00", candidate.Time);
        Assert.Equal(match.MatchDateTime, candidate.MatchDateTime);
    }

    [Fact]
    public void BuildForecastCandidates_CapturesCalibrationAndThresholdProvenance()
    {
        var match = CreateMatch();
        var thresholdService = new FakeThresholdTuningService
        {
            Decisions =
            {
                [PredictionMarket.BothTeamsScore] = new ThresholdDecision
                {
                    Threshold = 0.61,
                    ThresholdSource = "Tuned"
                }
            }
        };

        var service = new DataAnalyzerService(
            new FakeProbabilityCalculator
            {
                Btts = 0.64
            },
            new FakeCalibrationService((market, raw) =>
                market == PredictionMarket.BothTeamsScore
                    ? new CalibrationDecision { Probability = 0.67, CalibratorUsed = "Beta" }
                    : new CalibrationDecision { Probability = raw, CalibratorUsed = "Bucket" }),
            thresholdService,
            Options.Create(new PredictionSettings
            {
                BttsScoreThreshold = 0.55
            }));

        var candidates = service.SelectPublishedPredictions(service.BuildForecastCandidates([match]));
        var candidate = Assert.Single(candidates);

        Assert.Equal("Beta", candidate.CalibratorUsed);
        Assert.Equal(0.61, candidate.ThresholdUsed, 3);
        Assert.Equal("Tuned", candidate.ThresholdSource);
        Assert.True(candidate.WasPublished);
    }

    private static MatchData CreateMatch()
    {
        return new MatchData
        {
            Date = "12-03-2026",
            Time = "18:00",
            League = "Test League",
            HomeTeam = "Alpha",
            AwayTeam = "Beta"
        };
    }

    private sealed class FakeProbabilityCalculator : IProbabilityCalculator
    {
        public double Btts { get; set; }
        public double Over25 { get; set; }
        public double Draw { get; set; }
        public double HomeWin { get; set; }
        public double AwayWin { get; set; }

        public double CalculateBttsProbability(MatchData match) => Btts;
        public double CalculateOverTwoGoalsProbability(MatchData match) => Over25;
        public double CalculateDrawProbability(MatchData match) => Draw;
        public double CalculateHomeWinProbability(MatchData match) => HomeWin;
        public double CalculateAwayWinProbability(MatchData match) => AwayWin;
    }

    private sealed class FakeCalibrationService : ICalibrationService
    {
        private readonly Func<PredictionMarket, double, CalibrationDecision> _calibrate;

        public FakeCalibrationService(Func<PredictionMarket, double, double> calibrate)
            : this((market, raw) => new CalibrationDecision
            {
                Probability = calibrate(market, raw),
                CalibratorUsed = "Bucket"
            })
        {
        }

        public FakeCalibrationService(Func<PredictionMarket, double, CalibrationDecision> calibrate)
        {
            _calibrate = calibrate;
        }

        public double Calibrate(PredictionMarket market, double rawProbability) =>
            _calibrate(market, rawProbability).Probability;

        public CalibrationDecision CalibrateWithDecision(PredictionMarket market, double rawProbability) =>
            _calibrate(market, rawProbability);

        public Task RebuildProfilesAsync() => Task.CompletedTask;
    }

    private sealed class FakeThresholdTuningService : IThresholdTuningService
    {
        public Dictionary<PredictionMarket, ThresholdDecision> Decisions { get; } = new();

        public double GetThreshold(PredictionMarket market, double fallbackThreshold) =>
            GetThresholdDecision(market, fallbackThreshold).Threshold;

        public ThresholdDecision GetThresholdDecision(PredictionMarket market, double fallbackThreshold)
        {
            if (Decisions.TryGetValue(market, out var decision))
            {
                return decision;
            }

            return new ThresholdDecision
            {
                Threshold = fallbackThreshold,
                ThresholdSource = "Configured"
            };
        }

        public Task RebuildProfilesAsync() => Task.CompletedTask;
    }
}
