using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Infrastructure.Services;

/// <summary>
/// Converts the upstream AI workbook probabilities into raw market probabilities
/// without applying any historical weighting. Calibration happens separately.
/// </summary>
public class ProbabilityCalculator : IProbabilityCalculator
{
    public double CalculateBttsProbability(MatchData match)
    {
        var totalXg = EstimateTotalXg(match);
        if (totalXg <= 0)
            return 0.0;

        var (homeWin, draw, awayWin) = GetNormalizedOneX2(match);
        var (homeXg, awayXg) = ApportionXg(totalXg, homeWin, draw, awayWin);

        var pHomeScores = 1.0 - Math.Exp(-homeXg);
        var pAwayScores = 1.0 - Math.Exp(-awayXg);

        return Math.Clamp(pHomeScores * pAwayScores, 0.0, 1.0);
    }

    public double CalculateOverTwoGoalsProbability(MatchData match)
    {
        if (match.TryGetNormalizedOver25Pair(out var overUnder25))
            return Math.Clamp(overUnder25.over25, 0.0, 1.0);

        if (match.Over25() > 0)
            return Math.Clamp(match.Over25(), 0.0, 1.0);

        var totalXg = EstimateTotalXg(match);
        return totalXg <= 0
            ? 0.0
            : Math.Clamp(PoissonTailProbability(totalXg, 2), 0.0, 1.0);
    }

    public double CalculateDrawProbability(MatchData match)
    {
        var (_, draw, _) = GetNormalizedOneX2(match);
        return Math.Clamp(draw, 0.0, 1.0);
    }

    public double CalculateHomeWinProbability(MatchData match)
    {
        var (home, _, _) = GetNormalizedOneX2(match);
        return Math.Clamp(home, 0.0, 1.0);
    }

    public double CalculateAwayWinProbability(MatchData match)
    {
        var (_, _, away) = GetNormalizedOneX2(match);
        return Math.Clamp(away, 0.0, 1.0);
    }

    private static double EstimateTotalXg(MatchData match)
    {
        if (match.TryGetNormalizedOver25Pair(out var overUnder25))
            return InversePoissonOver(overUnder25.over25, threshold: 2);

        if (match.Over25() > 0)
            return InversePoissonOver(match.Over25(), threshold: 2);

        var lambdas = new List<double>();

        if (match.OverOnePointFive > 0)
            lambdas.Add(InversePoissonOver(match.OverOnePointFive, threshold: 1));

        if (match.Over35() > 0)
            lambdas.Add(InversePoissonOver(match.Over35(), threshold: 3));

        return lambdas.Count > 0 ? lambdas.Average() : 0.0;
    }

    private static (double home, double draw, double away) GetNormalizedOneX2(MatchData match)
    {
        if (match.TryGetNormalizedOneX2(out var normalized))
            return normalized;

        var total = Math.Max(match.HomeWin + match.Draw + match.AwayWin, 0.0);
        if (total > 0)
            return (match.HomeWin / total, match.Draw / total, match.AwayWin / total);

        return (0.0, 0.0, 0.0);
    }

    private static (double homeXg, double awayXg) ApportionXg(double totalXg, double homeWin, double draw, double awayWin)
    {
        var homeStrength = homeWin + (draw * 0.5);
        var awayStrength = awayWin + (draw * 0.5);
        var totalStrength = homeStrength + awayStrength;

        if (totalStrength <= 0)
            return (totalXg * 0.5, totalXg * 0.5);

        var homeShare = homeStrength / totalStrength;
        var awayShare = awayStrength / totalStrength;
        return (totalXg * homeShare, totalXg * awayShare);
    }

    private static double PoissonTailProbability(double lambda, int threshold)
    {
        var cdf = 0.0;
        for (var k = 0; k <= threshold; k++)
            cdf += PoissonProb(k, lambda);

        return 1.0 - cdf;
    }

    private static double InversePoissonOver(double targetProb, int threshold)
    {
        if (targetProb <= 0)
            return 0.0;

        var targetCdf = Math.Clamp(1.0 - targetProb, 0.01, 0.99);
        var lambda = Math.Max(threshold + 1.0 - targetCdf * (threshold + 1.0), 0.5);

        for (var i = 0; i < 20; i++)
        {
            var cdf = 0.0;
            for (var k = 0; k <= threshold; k++)
                cdf += PoissonProb(k, lambda);

            var error = cdf - targetCdf;
            if (Math.Abs(error) < 1e-6)
                break;

            var derivative = -PoissonProb(threshold, lambda);
            if (Math.Abs(derivative) < 1e-12)
                break;

            lambda = Math.Clamp(lambda - (error / derivative), 0.1, 8.0);
        }

        return Math.Clamp(lambda, 0.1, 8.0);
    }

    private static double PoissonProb(int k, double lambda)
    {
        if (lambda <= 0)
            return k == 0 ? 1.0 : 0.0;

        double factorial = 1.0;
        for (var i = 2; i <= k; i++)
            factorial *= i;

        return Math.Exp(-lambda) * Math.Pow(lambda, k) / factorial;
    }
}
