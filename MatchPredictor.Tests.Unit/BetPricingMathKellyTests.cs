using MatchPredictor.Domain.Models;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class BetPricingMathKellyTests
{
    [Fact]
    public void CalculateFractionalKellyStakeFraction_PositiveEdge_ReturnsQuarterKelly()
    {
        // p=0.60, odds 2.0 -> raw Kelly 0.2, quarter-Kelly stake 0.05.
        var stake = BetPricingMath.CalculateFractionalKellyStakeFraction(0.60, 2.0);

        Assert.Equal(0.05, stake, 6);
        Assert.Equal(BetPricingMath.DefaultKellyFraction, 0.25, 6);
    }

    [Fact]
    public void CalculateFractionalKellyStakeFraction_NoEdge_ReturnsZero()
    {
        // p=0.40, odds 2.0 -> raw Kelly <= 0.
        var stake = BetPricingMath.CalculateFractionalKellyStakeFraction(0.40, 2.0);

        Assert.Equal(0.0, stake, 6);
    }

    [Fact]
    public void CalculateFractionalKellyStakeFraction_InvalidOdds_ReturnsZero()
    {
        Assert.Equal(0.0, BetPricingMath.CalculateFractionalKellyStakeFraction(0.70, 1.0), 6);
        Assert.Equal(0.0, BetPricingMath.CalculateFractionalKellyStakeFraction(0.70, 0.5), 6);
    }
}
