using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Statistics;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class SourceMarketDeVigTests
{
    [Fact]
    public void ToFairProbabilities_WithFullOddsSets_DeVigsAllMarkets()
    {
        var fixture = new SourceMarketFixture
        {
            HomeWinOdds = 2.10,
            DrawOdds = 3.40,
            AwayWinOdds = 3.60,
            Over25Odds = 1.90,
            Under25Odds = 1.90,
            BttsYesOdds = 1.80,
            BttsNoOdds = 2.00
        };

        var fair = SourceMarketDeVig.ToFairProbabilities(fixture);

        Assert.NotNull(fair.HomeWin);
        Assert.NotNull(fair.Draw);
        Assert.NotNull(fair.AwayWin);
        Assert.Equal(1.0, fair.HomeWin!.Value + fair.Draw!.Value + fair.AwayWin!.Value, 9);

        Assert.NotNull(fair.Over25);
        Assert.NotNull(fair.Under25);
        Assert.Equal(1.0, fair.Over25!.Value + fair.Under25!.Value, 9);
        // Equal quoted odds must resolve to a fair coin flip after de-vig.
        Assert.Equal(0.5, fair.Over25.Value, 9);

        Assert.NotNull(fair.Btts);
        // Fair probabilities must sit below the vigged implied probabilities.
        Assert.True(fair.Btts!.Value < 1.0 / 1.80);
        Assert.Equal(SourceMarketDeVig.ShinPowerMethod, SourceMarketDeVig.ResolveMethod(fixture));
    }

    [Fact]
    public void ToFairProbabilities_WithoutOdds_FallsBackToNormalizedSourceProbabilities()
    {
        var fixture = new SourceMarketFixture
        {
            HomeWinProbability = 0.50,
            DrawProbability = 0.30,
            AwayWinProbability = 0.30
        };

        var fair = SourceMarketDeVig.ToFairProbabilities(fixture);

        Assert.NotNull(fair.HomeWin);
        Assert.Equal(1.0, fair.HomeWin!.Value + fair.Draw!.Value + fair.AwayWin!.Value, 9);
        Assert.Equal(0.50 / 1.10, fair.HomeWin.Value, 9);
        Assert.Null(fair.Over25);
        Assert.Null(fair.Btts);
        Assert.Equal(SourceMarketDeVig.ProportionalFallbackMethod, SourceMarketDeVig.ResolveMethod(fixture));
    }

    [Fact]
    public void ToFairProbabilities_WithPartialOddsSet_SkipsUnpriceableMarkets()
    {
        var fixture = new SourceMarketFixture
        {
            HomeWinOdds = 2.00,
            DrawOdds = 3.30,
            // Away odds missing → 1X2 cannot be de-vigged from odds and no probabilities exist.
            Over25Odds = 1.72,
            Under25Odds = 2.10
        };

        var fair = SourceMarketDeVig.ToFairProbabilities(fixture);

        Assert.Null(fair.HomeWin);
        Assert.Null(fair.Draw);
        Assert.Null(fair.AwayWin);
        Assert.NotNull(fair.Over25);
        Assert.Equal(1.0, fair.Over25!.Value + fair.Under25!.Value, 9);
        Assert.True(fair.HasAnySignal);
    }

    [Fact]
    public void ToFairProbabilities_WithNoSignals_ReturnsEmpty()
    {
        var fair = SourceMarketDeVig.ToFairProbabilities(new SourceMarketFixture());

        Assert.False(fair.HasAnySignal);
    }

    [Fact]
    public void ToFairProbabilities_ShinShadesLongshotsMoreThanProportional()
    {
        // Strong favourite vs longshot with a chunky margin — Shin should shade the
        // longshot's fair probability below the simple proportional normalization.
        var fixture = new SourceMarketFixture
        {
            HomeWinOdds = 1.30,
            DrawOdds = 5.50,
            AwayWinOdds = 11.00
        };

        var fair = SourceMarketDeVig.ToFairProbabilities(fixture);

        var implied = new[] { 1 / 1.30, 1 / 5.50, 1 / 11.00 };
        var proportionalAway = implied[2] / implied.Sum();

        Assert.True(fair.AwayWin!.Value <= proportionalAway + 1e-9);
    }
}
