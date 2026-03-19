using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Services;
using System.Reflection;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class ProbabilityCalculatorTests
{
    [Fact]
    public void CalculateBttsProbability_BlendsDirectBttsMarketWithGoalModel()
    {
        var calculator = new ProbabilityCalculator();
        var match = new MatchData
        {
            HomeWin = 0.46,
            Draw = 0.26,
            AwayWin = 0.28,
            OverTwoGoals = 0.63,
            UnderTwoGoals = 0.37,
            OverOnePointFive = 0.79,
            OverThreeGoals = 0.39,
            BttsYes = 0.69,
            BttsNo = 0.31,
            AhMinusHalfHome = 0.54,
            AhMinusHalfAway = 0.46,
            AhZeroHome = 0.57,
            AhZeroAway = 0.43
        };

        var probability = calculator.CalculateBttsProbability(match);

        Assert.InRange(probability, 0.60, 0.75);
    }

    [Fact]
    public void CalculateHomeWinProbability_UsesHandicapSignalsToStrengthenStrongFavourite()
    {
        var calculator = new ProbabilityCalculator();
        var match = new MatchData
        {
            HomeWin = 0.55,
            Draw = 0.24,
            AwayWin = 0.21,
            OverTwoGoals = 0.61,
            UnderTwoGoals = 0.39,
            OverOnePointFive = 0.77,
            AhMinusHalfHome = 0.67,
            AhMinusHalfAway = 0.33,
            AhMinusOneHome = 0.58,
            AhMinusOneAway = 0.42,
            AhZeroHome = 0.64,
            AhZeroAway = 0.36
        };

        var probability = calculator.CalculateHomeWinProbability(match);
        var awayProbability = calculator.CalculateAwayWinProbability(match);

        Assert.True(probability > 0.50);
        Assert.True(probability > awayProbability);
    }

    [Fact]
    public void CalculateAwayWinProbability_DampensLowConfidenceAwayInflation()
    {
        var calculator = new ProbabilityCalculator();
        var match = new MatchData
        {
            HomeWin = 0.43,
            Draw = 0.29,
            AwayWin = 0.28,
            OverTwoGoals = 0.56,
            UnderTwoGoals = 0.44,
            OverOnePointFive = 0.72,
            OverThreeGoals = 0.31,
            AhMinusHalfHome = 0.53,
            AhMinusHalfAway = 0.47,
            AhZeroHome = 0.49,
            AhZeroAway = 0.51,
            AhPlusHalfHome = 0.45,
            AhPlusHalfAway = 0.55
        };

        var awayProbability = calculator.CalculateAwayWinProbability(match);

        Assert.InRange(awayProbability, 0.24, 0.33);
        Assert.True(Math.Abs(awayProbability - 0.28) < 0.05);
    }

    [Fact]
    public void CalculateUnderTwoGoalsProbability_StaysCloseToUnderMarketWhenFixtureIsNotBalanced()
    {
        var calculator = new ProbabilityCalculator();
        var match = new MatchData
        {
            HomeWin = 0.56,
            Draw = 0.24,
            AwayWin = 0.20,
            OverTwoGoals = 0.58,
            UnderTwoGoals = 0.42,
            OverOnePointFive = 0.74,
            AhMinusHalfHome = 0.64,
            AhMinusHalfAway = 0.36,
            AhMinusOneHome = 0.56,
            AhMinusOneAway = 0.44,
            AhZeroHome = 0.61,
            AhZeroAway = 0.39
        };

        var underProbability = calculator.CalculateUnderTwoGoalsProbability(match);

        Assert.InRange(underProbability, 0.35, 0.50);
        Assert.True(Math.Abs(underProbability - 0.42) < 0.08);
    }

    [Fact]
    public void EstimateStrengthBias_UsesNonDrawLeanInsteadOfRepeatingRawOneX2Gap()
    {
        var method = typeof(ProbabilityCalculator).GetMethod(
            "EstimateStrengthBias",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var match = new MatchData();
        const double homeWin = 0.46;
        const double awayWin = 0.20;

        var bias = (double)method!.Invoke(null, [match, homeWin, awayWin])!;
        var expected = ((homeWin - awayWin) * 1.35 + (((homeWin - awayWin) / (homeWin + awayWin)) * 0.50)) / 1.85;

        Assert.Equal(expected, bias, 6);
        Assert.NotEqual(homeWin - awayWin, bias, 6);
    }
}
