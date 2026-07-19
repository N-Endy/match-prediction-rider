namespace MatchPredictor.Domain.Models;

public static class BetPricingMath
{
    /// <summary>Default fractional Kelly multiplier used across Analytics and Value Bets.</summary>
    public const double DefaultKellyFraction = 0.25;

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

    /// <summary>
    /// Fractional Kelly stake as a share of bankroll (0–1).
    /// Matches Analytics: raw Kelly from model probability and decimal odds, then multiplied by <paramref name="kellyFraction"/>.
    /// </summary>
    public static double CalculateFractionalKellyStakeFraction(
        double modelProbability,
        double decimalOdds,
        double kellyFraction = DefaultKellyFraction)
    {
        if (decimalOdds <= 1d || kellyFraction <= 0d)
        {
            return 0d;
        }

        var probability = Math.Clamp(modelProbability, 0d, 1d);
        var b = decimalOdds - 1d;
        if (b <= 0d)
        {
            return 0d;
        }

        var rawFraction = ((b * probability) - (1d - probability)) / b;
        if (rawFraction <= 0d)
        {
            return 0d;
        }

        return Math.Clamp(rawFraction * kellyFraction, 0d, 1d);
    }
}
