using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Statistics;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class EnsembleProbabilityBlenderTests
{
    [Fact]
    public void BlendLogit_OfEqualProbabilities_ReturnsThatProbability()
    {
        var blended = EnsembleProbabilityBlender.BlendLogit((0.6, 1.0), (0.6, 2.0));

        Assert.Equal(0.6, blended, 9);
    }

    [Fact]
    public void BlendLogit_IgnoresNullSignals()
    {
        var blended = EnsembleProbabilityBlender.BlendLogit((0.7, 1.0), (null, 5.0));

        Assert.Equal(0.7, blended, 9);
    }

    [Fact]
    public void BlendLogit_RespectsWeights()
    {
        // Heavier weight on the lower probability should pull the blend down.
        var blended = EnsembleProbabilityBlender.BlendLogit((0.8, 1.0), (0.2, 3.0));

        Assert.True(blended < 0.5);
    }

    [Fact]
    public void Blend_NormalizesOneX2AndComplementsUnder()
    {
        var market = new MatchProbabilities(Btts: 0.5, Over25: 0.55, Under25: 0.45, Draw: 0.25, HomeWin: 0.5, AwayWin: 0.25);
        var stat = new MatchProbabilities(Btts: 0.6, Over25: 0.6, Under25: 0.4, Draw: 0.3, HomeWin: 0.45, AwayWin: 0.25);

        var blended = EnsembleProbabilityBlender.Blend(market, null, stat, null);

        Assert.Equal(1.0, blended.HomeWin + blended.Draw + blended.AwayWin, 6);
        Assert.Equal(1.0, blended.Over25 + blended.Under25, 6);
    }

    [Fact]
    public void Blend_WithOnlyOneSignal_ReturnsThatSignalShape()
    {
        var stat = new MatchProbabilities(Btts: 0.62, Over25: 0.58, Under25: 0.42, Draw: 0.28, HomeWin: 0.5, AwayWin: 0.22);

        var blended = EnsembleProbabilityBlender.Blend(null, null, stat, null);

        // Over 2.5 should be preserved (single signal, logit blend of one value).
        Assert.Equal(0.58, blended.Over25, 6);
    }

    [Fact]
    public void Blend_WithBookmakerSignal_PullsTowardBookmakerProbability()
    {
        var market = new MatchProbabilities(Btts: 0.5, Over25: 0.50, Under25: 0.50, Draw: 0.25, HomeWin: 0.5, AwayWin: 0.25);
        var bookmaker = new PartialMatchProbabilities(Over25: 0.70);
        var weights = new EnsembleWeights { Bookmaker = 1.0, Market = 1.0, Base = 0, DixonColes = 0, Elo = 0 };

        var withBookmaker = EnsembleProbabilityBlender.Blend(bookmaker, market, null, null, null, weights);
        var withoutBookmaker = EnsembleProbabilityBlender.Blend(null, market, null, null, null, weights);

        Assert.True(withBookmaker.Over25 > withoutBookmaker.Over25);
        Assert.Equal(1.0, withBookmaker.Over25 + withBookmaker.Under25, 6);
    }

    [Fact]
    public void Blend_WithPartialBookmakerSignal_OnlyAffectsQuotedMarkets()
    {
        var market = new MatchProbabilities(Btts: 0.5, Over25: 0.5, Under25: 0.5, Draw: 0.25, HomeWin: 0.5, AwayWin: 0.25);
        var bookmaker = new PartialMatchProbabilities(HomeWin: 0.65, Draw: 0.2, AwayWin: 0.15);
        var weights = new EnsembleWeights { Bookmaker = 2.0, Market = 1.0, Base = 0, DixonColes = 0, Elo = 0 };

        var blended = EnsembleProbabilityBlender.Blend(bookmaker, market, null, null, null, weights);

        // 1X2 moves toward the bookmaker's favourite; totals/BTTS stay at the market value.
        Assert.True(blended.HomeWin > 0.5);
        Assert.Equal(0.5, blended.Over25, 6);
        Assert.Equal(0.5, blended.Btts, 6);
    }

    [Fact]
    public void Blend_LegacyOverload_MatchesBookmakerOverloadWithNullBookmaker()
    {
        var market = new MatchProbabilities(Btts: 0.5, Over25: 0.55, Under25: 0.45, Draw: 0.25, HomeWin: 0.5, AwayWin: 0.25);
        var stat = new MatchProbabilities(Btts: 0.6, Over25: 0.6, Under25: 0.4, Draw: 0.3, HomeWin: 0.45, AwayWin: 0.25);

        var legacy = EnsembleProbabilityBlender.Blend(market, null, stat, null);
        var explicitNull = EnsembleProbabilityBlender.Blend(
            bookmaker: null, market: market, baseModel: null, dixonColes: stat, elo: null);

        Assert.Equal(legacy, explicitNull);
    }
}
