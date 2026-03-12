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
                if (market != PredictionMarket.StraightWin)
                    return raw;

                return raw switch
                {
                    0.66 => 0.69,
                    0.62 => 0.72,
                    _ => raw
                };
            }),
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
        private readonly Func<PredictionMarket, double, double> _calibrate;

        public FakeCalibrationService(Func<PredictionMarket, double, double> calibrate)
        {
            _calibrate = calibrate;
        }

        public double Calibrate(PredictionMarket market, double rawProbability) => _calibrate(market, rawProbability);

        public Task RebuildProfilesAsync() => Task.CompletedTask;
    }
}
