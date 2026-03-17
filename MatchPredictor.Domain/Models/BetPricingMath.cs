namespace MatchPredictor.Domain.Models;

public static class BetPricingMath
{
    public static double? ConvertProbabilityToDecimalOdds(double? probability)
    {
        if (probability is null || probability <= 0d || probability > 1d)
        {
            return null;
        }

        return Math.Round(1d / probability.Value, 4);
    }

    public static double? ConvertDecimalOddsToProbability(double? decimalOdds)
    {
        if (decimalOdds is null || decimalOdds <= 1d)
        {
            return null;
        }

        return Math.Round(1d / decimalOdds.Value, 6);
    }

    public static double? CalculateExpectedValuePercent(double? modelProbability, double? decimalOdds)
    {
        if (modelProbability is null || decimalOdds is null || modelProbability <= 0d || decimalOdds <= 1d)
        {
            return null;
        }

        return Math.Round((modelProbability.Value * decimalOdds.Value) - 1d, 6);
    }

    public static double? CalculateClosingLineValuePercent(double? publishOdds, double? closingOdds)
    {
        if (publishOdds is null || closingOdds is null || publishOdds <= 1d || closingOdds <= 1d)
        {
            return null;
        }

        return Math.Round((publishOdds.Value / closingOdds.Value) - 1d, 6);
    }
}
