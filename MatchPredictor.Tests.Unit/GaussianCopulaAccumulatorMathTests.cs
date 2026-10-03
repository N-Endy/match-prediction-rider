using MatchPredictor.Domain.Models;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class GaussianCopulaAccumulatorMathTests
{
    [Fact]
    public void EvaluateAccumulator_ZeroCorrelation_EqualsIndependentProbabilityProduct()
    {
        var probs = new[] { 0.60, 0.50, 0.70 };
        var expectedIndep = 0.60 * 0.50 * 0.70; // 0.21

        var result = GaussianCopulaAccumulatorMath.EvaluateAccumulator(probs, correlationRho: 0.0);

        Assert.Equal(expectedIndep, result.IndependentProbability, 5);
        Assert.Equal(expectedIndep, result.CopulaProbability, 5);
        Assert.Equal(1.0, result.TailDependenceRatio, 3);
    }

    [Fact]
    public void EvaluateAccumulator_PositiveCorrelation_IncreasesJointTailProbability()
    {
        // When individual events are positively correlated (e.g. general high scoring day across fixtures),
        // the probability that all 4 events hit simultaneously is higher than naive independent multiplication.
        var probs = new[] { 0.65, 0.60, 0.70, 0.55 };
        var indep = probs.Aggregate(1.0, (acc, p) => acc * p);

        var result = GaussianCopulaAccumulatorMath.EvaluateAccumulator(probs, correlationRho: 0.15);

        Assert.Equal(indep, result.IndependentProbability, 4);
        Assert.True(result.CopulaProbability > indep,
            $"Expected copula prob ({result.CopulaProbability}) > independent prob ({indep})");
        Assert.True(result.TailDependenceRatio > 1.05);
    }

    [Fact]
    public void Probit_InvertsNormalCdfAccurately()
    {
        var testZValues = new[] { -2.5, -1.96, -1.0, 0.0, 1.0, 1.96, 2.5 };

        foreach (var z in testZValues)
        {
            var p = ModelPromotionGate.NormalCdf(z);
            var invertedZ = GaussianCopulaAccumulatorMath.Probit(p);
            Assert.Equal(z, invertedZ, 2);
        }
    }

    [Fact]
    public void CalculateEv_ReflectsAccurateCopulaExpectedValue()
    {
        var probs = new[] { 0.70, 0.70 };
        var odds = 2.50;

        var (indepEv, copulaEv) = GaussianCopulaAccumulatorMath.CalculateEv(probs, odds, correlationRho: 0.20);

        // Indep prob is 0.49 -> EV = 0.49 * 2.50 - 1 = 1.225 - 1 = +0.225
        Assert.Equal(0.49 * 2.50 - 1.0, indepEv, 4);
        // Under positive correlation (0.20), joint probability is higher, so Copula EV is higher
        Assert.True(copulaEv > indepEv);
    }
}
