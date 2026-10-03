using MatchPredictor.Domain.Models;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class FeatureDriftMathTests
{
    [Fact]
    public void EvaluateFeature_IdenticalDistributions_ReturnsNegligiblePsiAndZeroWasserstein()
    {
        var sample = Enumerable.Range(1, 100).Select(i => (double)i).ToList();

        var result = FeatureDriftMath.EvaluateFeature("HomeXG", sample, sample);

        Assert.Equal("HomeXG", result.FeatureName);
        Assert.True(result.PopulationStabilityIndex < 0.05, $"Expected PSI < 0.05, got {result.PopulationStabilityIndex}");
        Assert.Equal(0.0, result.WassersteinDistance, 5);
        Assert.Equal(DriftSeverity.None, result.Severity);
    }

    [Fact]
    public void EvaluateFeature_ShiftedDistribution_DetectsDriftAndCalculatesExpectedWasserstein()
    {
        var reference = Enumerable.Range(1, 200).Select(i => (double)i).ToList();
        // Shift distribution by +50 units
        var target = reference.Select(x => x + 50.0).ToList();

        var result = FeatureDriftMath.EvaluateFeature("RollingGoals", reference, target);

        Assert.True(result.PopulationStabilityIndex > FeatureDriftMath.SignificantPsiThreshold,
            $"Expected significant PSI, got {result.PopulationStabilityIndex}");
        Assert.Equal(DriftSeverity.Significant, result.Severity);
        // For uniform shift, Wasserstein distance equals the shift magnitude (50.0)
        Assert.Equal(50.0, result.WassersteinDistance, 1);
    }

    [Fact]
    public void CalculatePsi_ModerateShift_CategorizesAsModerate()
    {
        var rng = new Random(42);
        // Base normal-like distribution around 1.5
        var reference = Enumerable.Range(0, 500).Select(_ => 1.5 + rng.NextDouble() * 0.5).ToList();
        // Slightly shifted target
        var target = Enumerable.Range(0, 500).Select(_ => 1.65 + rng.NextDouble() * 0.5).ToList();

        var result = FeatureDriftMath.EvaluateFeature("RestDays", reference, target);

        Assert.True(result.PopulationStabilityIndex > 0.0);
        Assert.True(result.WassersteinDistance > 0.10);
    }

    [Fact]
    public void EvaluateFeature_EmptyOrDegenerateInput_HandlesGracefully()
    {
        var empty = Array.Empty<double>();
        var reference = new[] { 1.0, 2.0, 3.0 };

        var result = FeatureDriftMath.EvaluateFeature("EmptyFeature", reference, empty);

        Assert.Equal(0.0, result.PopulationStabilityIndex);
        Assert.Equal(0.0, result.WassersteinDistance);
        Assert.Equal(DriftSeverity.None, result.Severity);
    }
}
