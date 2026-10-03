using MatchPredictor.Domain.Models;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class ModelPromotionGateTests
{
    [Fact]
    public void Evaluate_SampleCountBelowMinimum_RejectsWithoutTesting()
    {
        var champ = Enumerable.Range(0, 50).Select(_ => 0.25).ToList();
        var chall = Enumerable.Range(0, 50).Select(_ => 0.20).ToList();

        var result = ModelPromotionGate.Evaluate(champ, chall, minSamples: 100);

        Assert.False(result.ShouldPromote);
        Assert.Equal(50, result.SampleCount);
        Assert.Equal(1.0, result.PValue);
        Assert.Contains("Insufficient samples", result.Summary);
    }

    [Fact]
    public void Evaluate_SignificantChallengerImprovement_PromotesWithLowPValue()
    {
        var rng = new Random(123);
        var champ = new List<double>();
        var chall = new List<double>();

        for (var i = 0; i < 150; i++)
        {
            var baseLoss = 0.22 + rng.NextDouble() * 0.05;
            // Challenger is consistently 0.02 better
            champ.Add(baseLoss + 0.02);
            chall.Add(baseLoss);
        }

        var result = ModelPromotionGate.Evaluate(champ, chall, minSamples: 100, alpha: 0.05);

        Assert.True(result.ShouldPromote);
        Assert.True(result.Improvement > 0.015);
        Assert.True(result.PValue < 0.001, $"Expected p < 0.001, got {result.PValue}");
        Assert.Contains("cleared promotion gate", result.Summary);
    }

    [Fact]
    public void Evaluate_ChallengerWorseOrEqual_RejectsPromotion()
    {
        var champ = Enumerable.Repeat(0.20, 120).ToList();
        var chall = Enumerable.Repeat(0.25, 120).ToList();

        var result = ModelPromotionGate.Evaluate(champ, chall, minSamples: 100);

        Assert.False(result.ShouldPromote);
        Assert.True(result.Improvement < 0);
        Assert.Equal(1.0, result.PValue);
    }

    [Fact]
    public void NormalCdf_StandardValues_MatchesExpectedProbability()
    {
        Assert.Equal(0.5, ModelPromotionGate.NormalCdf(0.0), 3);
        Assert.True(ModelPromotionGate.NormalCdf(1.96) > 0.975 - 0.005);
        Assert.True(ModelPromotionGate.NormalCdf(-1.96) < 0.025 + 0.005);
    }
}
