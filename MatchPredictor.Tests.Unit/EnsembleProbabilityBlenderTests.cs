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
}
