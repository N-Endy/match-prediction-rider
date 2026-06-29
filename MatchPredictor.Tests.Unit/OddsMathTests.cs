using MatchPredictor.Infrastructure.Statistics;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class OddsMathTests
{
    [Fact]
    public void Overround_DetectsBookmakerMargin()
    {
        // 1X2 market with a ~5% margin baked in.
        var odds = new[] { 2.10, 3.40, 3.60 };

        var overround = OddsMath.Overround(odds);

        Assert.True(overround > 0.0);
        Assert.InRange(overround, 0.02, 0.12);
    }

    [Fact]
    public void FairProbabilitiesMultiplicative_SumsToOne_AndRemovesMargin()
    {
        var odds = new[] { 2.10, 3.40, 3.60 };

        var fair = OddsMath.FairProbabilitiesMultiplicative(odds);

        Assert.Equal(1.0, fair.Sum(), 9);
        // Each fair probability must be below its (inflated) implied probability.
        for (var i = 0; i < odds.Length; i++)
        {
            Assert.True(fair[i] < OddsMath.ImpliedProbability(odds[i]));
        }
    }

    [Fact]
    public void FairProbabilitiesPower_SumsToOne_AndPreservesOrdering()
    {
        var odds = new[] { 1.80, 3.60, 4.50 };

        var fair = OddsMath.FairProbabilitiesPower(odds);

        Assert.Equal(1.0, fair.Sum(), 9);
        // The favourite (lowest odds) must remain the most probable outcome.
        Assert.True(fair[0] > fair[1]);
        Assert.True(fair[1] > fair[2]);
    }

    [Fact]
    public void FairProbabilitiesShin_SumsToOne_AndStaysWithinUnitInterval()
    {
        var odds = new[] { 2.05, 3.50, 3.70 };

        var fair = OddsMath.FairProbabilitiesShin(odds);

        Assert.Equal(1.0, fair.Sum(), 9);
        Assert.All(fair, probability => Assert.InRange(probability, 0.0, 1.0));
    }

    [Fact]
    public void FairProbabilities_WithNoMargin_ReturnNormalizedInput()
    {
        // A perfectly fair two-way market (no overround).
        var odds = new[] { 2.00, 2.00 };

        var multiplicative = OddsMath.FairProbabilitiesMultiplicative(odds);
        var power = OddsMath.FairProbabilitiesPower(odds);

        Assert.Equal(0.5, multiplicative[0], 6);
        Assert.Equal(0.5, power[0], 6);
    }
}
