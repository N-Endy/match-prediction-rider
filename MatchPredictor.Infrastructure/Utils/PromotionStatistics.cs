namespace MatchPredictor.Infrastructure.Utils;

public static class PromotionStatistics
{
    public static double WeightedBrier(IReadOnlyList<(double Predicted, bool Outcome, double Weight)> samples)
    {
        if (samples.Count == 0)
        {
            return 0.0;
        }

        var totalWeight = samples.Sum(sample => sample.Weight);
        if (totalWeight <= 0)
        {
            return 0.0;
        }

        return samples.Sum(sample =>
            Math.Pow(Math.Clamp(sample.Predicted, 0.0, 1.0) - (sample.Outcome ? 1.0 : 0.0), 2) *
            sample.Weight) / totalWeight;
    }
}
