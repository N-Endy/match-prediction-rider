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
        var model = BuildProbabilityModel(match);
        return model.Btts;
    }

    public double CalculateOverTwoGoalsProbability(MatchData match)
    {
        var model = BuildProbabilityModel(match);
        return model.Over25;
    }

    public double CalculateUnderTwoGoalsProbability(MatchData match)
    {
        var model = BuildProbabilityModel(match);
        return Math.Clamp(1.0 - model.Over25, 0.0, 1.0);
    }

    public double CalculateHomeWinProbability(MatchData match)
    {
        var model = BuildProbabilityModel(match);
        return model.HomeWin;
    }

    public double CalculateAwayWinProbability(MatchData match)
    {
        var model = BuildProbabilityModel(match);
        return model.AwayWin;
    }

    private static ProbabilityModel BuildProbabilityModel(MatchData match)
    {
        var (homeWinSource, drawSource, awayWinSource) = GetNormalizedOneX2(match);
        var totalXg = EstimateTotalXg(match);
        if (totalXg <= 0)
        {
            return new ProbabilityModel(
                Btts: match.TryGetNormalizedBttsPair(out var bttsPair) ? bttsPair.yes : 0.0,
                Over25: match.TryGetNormalizedOver25Pair(out var overUnder25) ? overUnder25.over25 : 0.0,
                Draw: drawSource,
                HomeWin: homeWinSource,
                AwayWin: awayWinSource);
        }

        var strengthBias = EstimateStrengthBias(match, homeWinSource, drawSource, awayWinSource);
        var homeShare = Math.Clamp(0.5 + (strengthBias * (0.33 * (1.0 - (drawSource * 0.35)))), 0.18, 0.82);
        var homeXg = Math.Max(totalXg * homeShare, 0.05);
        var awayXg = Math.Max(totalXg - homeXg, 0.05);
        var poisson = ComputePoissonOutcomeModel(homeXg, awayXg);

        var over25 = BlendAvailable(
            (match.TryGetNormalizedOver25Pair(out var overPair) ? overPair.over25 : (double?)null, 0.72),
            (poisson.Over25, 0.28));

        var provisionalHomeWin = BlendAvailable(
            (homeWinSource > 0 ? homeWinSource : (double?)null, 0.48),
            (TryEstimateHandicapWinProbability(match, drawSource, isHome: true), 0.22),
            (poisson.HomeWin, 0.30));
        var provisionalAwayWin = BlendAvailable(
            (awayWinSource > 0 ? awayWinSource : (double?)null, 0.48),
            (TryEstimateHandicapWinProbability(match, drawSource, isHome: false), 0.22),
            (poisson.AwayWin, 0.30));
        var provisionalDraw = BlendAvailable(
            (drawSource > 0 ? drawSource : (double?)null, 0.62),
            (poisson.Draw, 0.38));

        var (homeWin, draw, awayWin) = ApplyAnchoredOneX2Blend(
            match,
            homeWinSource,
            drawSource,
            awayWinSource,
            provisionalHomeWin,
            provisionalDraw,
            provisionalAwayWin,
            strengthBias);

        var marginStrength = EstimateMarginStrength(match);
        var competitiveness = Math.Clamp(1.0 - (Math.Abs(strengthBias) * 0.90) - (marginStrength * 0.35), 0.35, 1.0);
        var goalSupport = BlendAvailable(
            (match.OverOnePointFive > 0 ? Math.Clamp(match.OverOnePointFive, 0.0, 1.0) : (double?)null, 0.30),
            (over25, 0.40),
            (poisson.Over25, 0.30));
        var directBtts = match.TryGetNormalizedBttsPair(out var normalizedBtts)
            ? normalizedBtts.yes
            : (double?)null;
        var heuristicBtts = Math.Clamp(
            (poisson.Btts * (0.88 + (0.22 * competitiveness))) +
            ((goalSupport - 0.50) * 0.08),
            0.0,
            1.0);
        var btts = BlendAvailable(
            (directBtts, 0.58),
            (heuristicBtts, 0.32),
            (Math.Clamp(goalSupport * (0.70 + (0.25 * competitiveness)), 0.0, 1.0), 0.10));

        return new ProbabilityModel(
            Btts: Math.Clamp(btts, 0.0, 1.0),
            Over25: Math.Clamp(over25, 0.0, 1.0),
            Draw: Math.Clamp(draw, 0.0, 1.0),
            HomeWin: Math.Clamp(homeWin, 0.0, 1.0),
            AwayWin: Math.Clamp(awayWin, 0.0, 1.0));
    }

    private static double EstimateTotalXg(MatchData match)
    {
        var weightedLambdas = new List<(double Lambda, double Weight)>();

        if (match.TryGetNormalizedOver25Pair(out var overUnder25))
        {
            weightedLambdas.Add((InversePoissonOver(overUnder25.over25, threshold: 2), 1.45));
        }
        else if (match.Over25() > 0)
        {
            weightedLambdas.Add((InversePoissonOver(match.Over25(), threshold: 2), 1.20));
        }

        if (match.OverOnePointFive > 0)
        {
            weightedLambdas.Add((InversePoissonOver(match.OverOnePointFive, threshold: 1), 0.90));
        }

        if (match.Over35() > 0)
        {
            weightedLambdas.Add((InversePoissonOver(match.Over35(), threshold: 3), 0.75));
        }

        if (weightedLambdas.Count == 0)
        {
            return 0.0;
        }

        var totalWeight = weightedLambdas.Sum(item => item.Weight);
        return totalWeight > 0
            ? weightedLambdas.Sum(item => item.Lambda * item.Weight) / totalWeight
            : 0.0;
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

    private static double EstimateStrengthBias(MatchData match, double homeWin, double draw, double awayWin)
    {
        var weightedSignals = new List<(double Value, double Weight)>
        {
            (homeWin - awayWin, 1.35),
            ((homeWin + (draw * 0.5)) - (awayWin + (draw * 0.5)), 0.50)
        };

        if (match.TryGetNormalizedAhZeroPair(out var ahZero))
        {
            weightedSignals.Add((ahZero.home - ahZero.away, 0.70));
        }

        if (match.TryGetNormalizedAhMinusHalfPair(out var ahMinusHalf))
        {
            weightedSignals.Add((ahMinusHalf.home - ahMinusHalf.away, 1.00));
        }

        if (match.TryGetNormalizedAhPlusHalfPair(out var ahPlusHalf))
        {
            weightedSignals.Add((ahPlusHalf.home - ahPlusHalf.away, 0.85));
        }

        if (match.TryGetNormalizedAhMinusOnePair(out var ahMinusOne))
        {
            weightedSignals.Add((ahMinusOne.home - ahMinusOne.away, 1.15));
        }

        var totalWeight = weightedSignals.Sum(item => item.Weight);
        if (totalWeight <= 0)
        {
            return 0.0;
        }

        return Math.Clamp(weightedSignals.Sum(item => item.Value * item.Weight) / totalWeight, -1.0, 1.0);
    }

    private static double EstimateMarginStrength(MatchData match)
    {
        if (!match.TryGetNormalizedAhMinusOnePair(out var ahMinusOne))
        {
            return 0.0;
        }

        return Math.Abs(ahMinusOne.home - ahMinusOne.away);
    }

    private static double? TryEstimateHandicapWinProbability(MatchData match, double drawProbability, bool isHome)
    {
        var signals = new List<(double Probability, double Weight)>();

        if (match.TryGetNormalizedAhMinusHalfPair(out var ahMinusHalf))
        {
            signals.Add((isHome ? ahMinusHalf.home : ahMinusHalf.away, 1.00));
        }

        if (match.TryGetNormalizedAhPlusHalfPair(out var ahPlusHalf))
        {
            signals.Add((isHome ? 1.0 - ahPlusHalf.away : 1.0 - ahPlusHalf.home, 0.75));
        }

        if (match.TryGetNormalizedAhZeroPair(out var ahZero))
        {
            signals.Add((((isHome ? ahZero.home : ahZero.away) * (1.0 - drawProbability)), 0.65));
        }

        if (match.TryGetNormalizedAhMinusOnePair(out var ahMinusOne))
        {
            var winByMargin = isHome ? ahMinusOne.home : ahMinusOne.away;
            signals.Add((Math.Clamp(winByMargin + 0.15, 0.0, 1.0), 0.35));
        }

        if (signals.Count == 0)
        {
            return null;
        }

        return BlendAvailable(signals.Select(signal => ((double?)signal.Probability, signal.Weight)).ToArray());
    }

    private static (double home, double draw, double away) ApplyAnchoredOneX2Blend(
        MatchData match,
        double homeSource,
        double drawSource,
        double awaySource,
        double blendedHome,
        double blendedDraw,
        double blendedAway,
        double strengthBias)
    {
        var home = homeSource > 0
            ? homeSource + ((blendedHome - homeSource) * DetermineSideAdjustmentScale(match, homeSource, awaySource, strengthBias, isHome: true))
            : blendedHome;

        var away = awaySource > 0
            ? awaySource + ((blendedAway - awaySource) * DetermineSideAdjustmentScale(match, awaySource, homeSource, strengthBias, isHome: false))
            : blendedAway;

        var draw = drawSource > 0
            ? drawSource + ((blendedDraw - drawSource) * DetermineDrawAdjustmentScale(homeSource, drawSource, awaySource))
            : blendedDraw;

        return NormalizeOneX2(home, draw, away);
    }

    private static double DetermineSideAdjustmentScale(
        MatchData match,
        double sideSource,
        double opposingSource,
        double strengthBias,
        bool isHome)
    {
        var sourceGap = Math.Abs(sideSource - opposingSource);
        var handicapSupport = EstimateHandicapSupport(match, isHome);
        var strengthSupport = isHome ? Math.Max(strengthBias, 0.0) : Math.Max(-strengthBias, 0.0);

        var scale = sideSource switch
        {
            >= 0.78 => 0.34,
            >= 0.68 => 0.24,
            >= 0.58 => 0.16,
            >= 0.48 => 0.10,
            _ => 0.06
        };

        if (!isHome && sideSource < 0.60)
        {
            scale *= 0.70;
        }

        if (sourceGap < 0.10)
        {
            scale *= 0.75;
        }

        if (handicapSupport < -0.05)
        {
            scale *= 0.50;
        }
        else if (handicapSupport > 0.15 || strengthSupport > 0.22)
        {
            scale += 0.05;
        }

        return Math.Clamp(scale, 0.03, 0.40);
    }

    private static double DetermineDrawAdjustmentScale(double homeSource, double drawSource, double awaySource)
    {
        var sourceGap = Math.Abs(homeSource - awaySource);
        var scale = drawSource switch
        {
            >= 0.30 => 0.16,
            >= 0.24 => 0.13,
            _ => 0.10
        };

        if (sourceGap < 0.10)
        {
            scale += 0.03;
        }
        else if (sourceGap > 0.18)
        {
            scale *= 0.65;
        }

        return Math.Clamp(scale, 0.07, 0.20);
    }

    private static double EstimateHandicapSupport(MatchData match, bool isHome)
    {
        var signals = new List<(double Value, double Weight)>();

        if (match.TryGetNormalizedAhZeroPair(out var ahZero))
        {
            signals.Add((((isHome ? ahZero.home : ahZero.away) - (isHome ? ahZero.away : ahZero.home)), 0.55));
        }

        if (match.TryGetNormalizedAhMinusHalfPair(out var ahMinusHalf))
        {
            signals.Add((((isHome ? ahMinusHalf.home : ahMinusHalf.away) - (isHome ? ahMinusHalf.away : ahMinusHalf.home)), 0.95));
        }

        if (match.TryGetNormalizedAhPlusHalfPair(out var ahPlusHalf))
        {
            signals.Add((((isHome ? ahPlusHalf.home : ahPlusHalf.away) - (isHome ? ahPlusHalf.away : ahPlusHalf.home)), 0.70));
        }

        if (match.TryGetNormalizedAhMinusOnePair(out var ahMinusOne))
        {
            signals.Add((((isHome ? ahMinusOne.home : ahMinusOne.away) - (isHome ? ahMinusOne.away : ahMinusOne.home)), 1.05));
        }

        var totalWeight = signals.Sum(signal => signal.Weight);
        if (totalWeight <= 0)
        {
            return 0.0;
        }

        return signals.Sum(signal => signal.Value * signal.Weight) / totalWeight;
    }

    private static (double home, double draw, double away) NormalizeOneX2(double home, double draw, double away)
    {
        home = Math.Clamp(home, 0.0, 1.0);
        draw = Math.Clamp(draw, 0.0, 1.0);
        away = Math.Clamp(away, 0.0, 1.0);

        var total = home + draw + away;
        if (total <= 0)
        {
            return (0.0, 0.0, 0.0);
        }

        return (home / total, draw / total, away / total);
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

    private static PoissonOutcomeModel ComputePoissonOutcomeModel(double homeLambda, double awayLambda)
    {
        const int goalCap = 10;
        var homeGoalProbabilities = BuildPoissonDistribution(homeLambda, goalCap);
        var awayGoalProbabilities = BuildPoissonDistribution(awayLambda, goalCap);

        var homeWin = 0.0;
        var draw = 0.0;
        var over25 = 0.0;
        var btts = 0.0;

        for (var homeGoals = 0; homeGoals <= goalCap; homeGoals++)
        {
            for (var awayGoals = 0; awayGoals <= goalCap; awayGoals++)
            {
                var probability = homeGoalProbabilities[homeGoals] * awayGoalProbabilities[awayGoals];

                if (homeGoals > awayGoals)
                {
                    homeWin += probability;
                }
                else if (homeGoals == awayGoals)
                {
                    draw += probability;
                }

                if (homeGoals + awayGoals >= 3)
                {
                    over25 += probability;
                }

                if (homeGoals > 0 && awayGoals > 0)
                {
                    btts += probability;
                }
            }
        }

        var awayWin = Math.Clamp(1.0 - homeWin - draw, 0.0, 1.0);
        return new PoissonOutcomeModel(
            HomeWin: Math.Clamp(homeWin, 0.0, 1.0),
            Draw: Math.Clamp(draw, 0.0, 1.0),
            AwayWin: awayWin,
            Over25: Math.Clamp(over25, 0.0, 1.0),
            Btts: Math.Clamp(btts, 0.0, 1.0));
    }

    private static double[] BuildPoissonDistribution(double lambda, int goalCap)
    {
        var distribution = new double[goalCap + 1];
        var runningTotal = 0.0;

        for (var goals = 0; goals < goalCap; goals++)
        {
            distribution[goals] = PoissonProb(goals, lambda);
            runningTotal += distribution[goals];
        }

        distribution[goalCap] = Math.Max(0.0, 1.0 - runningTotal);
        return distribution;
    }

    private static double BlendAvailable(params (double? Probability, double Weight)[] inputs)
    {
        var available = inputs
            .Where(input => input.Probability.HasValue)
            .ToList();

        if (available.Count == 0)
        {
            return 0.0;
        }

        var totalWeight = available.Sum(input => input.Weight);
        if (totalWeight <= 0)
        {
            return available.Average(input => input.Probability!.Value);
        }

        return available.Sum(input => input.Probability!.Value * input.Weight) / totalWeight;
    }

    private sealed record ProbabilityModel(
        double Btts,
        double Over25,
        double Draw,
        double HomeWin,
        double AwayWin);

    private sealed record PoissonOutcomeModel(
        double HomeWin,
        double Draw,
        double AwayWin,
        double Over25,
        double Btts);
}
