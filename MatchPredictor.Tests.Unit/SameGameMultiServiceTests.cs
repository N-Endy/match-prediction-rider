using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Services;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class SameGameMultiServiceTests
{
    private readonly SameGameMultiService _service = new();

    [Fact]
    public void Evaluate_MutuallyExclusiveLegs_ReturnsInvalidAndZeroProbability()
    {
        var legs = new SameGameMultiLeg[]
        {
            new(SameGameMultiMarket.HomeWin, "Home Win"),
            new(SameGameMultiMarket.Under15Goals, "Under 1.5 Goals"),
            new(SameGameMultiMarket.BothTeamsScoreYes, "BTTS Yes")
        };
        // A Home Win with BTTS Yes requires at least 2-1 (3 goals), which contradicts Under 1.5 (max 1 goal)!

        var result = _service.Evaluate(homeLambda: 1.8, awayMu: 1.1, rho: -0.03, legs);

        Assert.False(result.IsValid);
        Assert.Equal(0.0, result.ExactJointProbability);
        Assert.Contains("Mutually exclusive", result.RejectionReason);
    }

    [Fact]
    public void Evaluate_PositivelyCorrelatedLegs_AccuratelyReflectsPositiveCorrelation()
    {
        // For a strong home favorite (lambda = 2.4, mu = 0.7), Home Win and Home Over 1.5 are strongly positively correlated.
        var legs = new SameGameMultiLeg[]
        {
            new(SameGameMultiMarket.HomeWin, "Home Win"),
            new(SameGameMultiMarket.HomeOver15Goals, "Home Score 2+ Goals")
        };

        var result = _service.Evaluate(homeLambda: 2.4, awayMu: 0.7, rho: -0.05, legs);

        Assert.True(result.IsValid);
        Assert.True(result.ExactJointProbability > 0);
        Assert.True(result.FairDecimalOdds > 1.0);
        // Positive correlation: joint probability is higher than naive independent multiplication
        Assert.True(result.CorrelationFactor > 1.0,
            $"Expected CorrelationFactor > 1.0, got {result.CorrelationFactor}");
    }

    [Fact]
    public void Evaluate_MonotonicityInvariant_JointProbabilityNeverExceedsSingleLegProbability()
    {
        var legHomeWin = new SameGameMultiLeg(SameGameMultiMarket.HomeWin, "Home Win");
        var legOver25 = new SameGameMultiLeg(SameGameMultiMarket.Over25Goals, "Over 2.5 Goals");

        var singleHomeWin = _service.Evaluate(1.7, 1.2, -0.03, [legHomeWin]);
        var singleOver25 = _service.Evaluate(1.7, 1.2, -0.03, [legOver25]);
        var combo = _service.Evaluate(1.7, 1.2, -0.03, [legHomeWin, legOver25]);

        Assert.True(combo.IsValid);
        Assert.True(combo.ExactJointProbability <= singleHomeWin.ExactJointProbability);
        Assert.True(combo.ExactJointProbability <= singleOver25.ExactJointProbability);
    }

    [Fact]
    public void FindCuratedCombinations_ProducesValidCombinationsWithinRequestedOddsBand()
    {
        var curated = _service.FindCuratedCombinations(
            homeLambda: 1.9,
            awayMu: 1.2,
            rho: -0.04,
            minFairOdds: 2.0,
            maxFairOdds: 8.0);

        Assert.NotEmpty(curated);
        Assert.All(curated, c =>
        {
            Assert.True(c.IsValid);
            Assert.True(c.FairDecimalOdds >= 2.0 && c.FairDecimalOdds <= 8.0);
            Assert.True(c.Legs.Count >= 2);
        });
    }

    [Fact]
    public void FindCuratedCombinations_ForAwayFavorite_IncludesAwayFavoredCombinations()
    {
        var curated = _service.FindCuratedCombinations(
            homeLambda: 0.6,
            awayMu: 2.1,
            rho: -0.04,
            minFairOdds: 2.0,
            maxFairOdds: 6.0);

        Assert.NotEmpty(curated);
        var hasAwayCombo = curated.Any(c => c.Legs.Any(l =>
            l.Market is SameGameMultiMarket.AwayWin or SameGameMultiMarket.DoubleChanceX2));
        Assert.True(hasAwayCombo, "Expected curated combinations for an away favorite to include Away Win or Double Chance X2.");
    }
}
