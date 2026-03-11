using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Infrastructure.Services;

/// <summary>
/// Data-driven probability calculator using Expected Goals (xG) and Poisson distributions.
/// Designed for pre-processed True Probabilities (margin-free data).
/// Uses bivariate Poisson score matrices for mathematically consistent W/D/L probabilities.
/// </summary>
public class ProbabilityCalculator : IProbabilityCalculator
{
    private readonly PredictionSettings _settings;

    public ProbabilityCalculator(Microsoft.Extensions.Options.IOptions<PredictionSettings> options)
    {
        _settings = options.Value;
    }

    private double GetHistoricalWeight(List<ModelAccuracy> accuracies, string category, params (string MetricName, double MetricValue)[] fallbacks)
    {
        if (accuracies == null || accuracies.Count == 0 || fallbacks == null || fallbacks.Length == 0) return 1.0;

        foreach (var (metricName, metricValue) in fallbacks)
        {
            if (metricValue <= 0) continue;

            var profile = accuracies.FirstOrDefault(a => 
                a.Category == category && 
                a.MetricName == metricName && 
                metricValue >= a.MetricRangeStart && 
                metricValue < a.MetricRangeEnd);

            if (profile != null && profile.TotalPredictions >= 5)
            {
                var weight = 1.0 + (profile.AccuracyPercentage - 0.50);
                return Math.Clamp(weight, 0.7, 1.3);
            }
        }

        return 1.0;
    }

    public double CalculateBttsProbability(MatchData match, List<ModelAccuracy> accuracies)
    {
        var trueOver25 = GetTrueOver25(match);
        if (trueOver25 <= 0 || match.HomeWin <= 0) return 0.0;  

        // 1. Derive Total xG using inverse Poisson CDF (Fix #1)
        var totalXg = EstimateTotalXg(trueOver25, match.OverOnePointFive);

        // 2. Apportion xG including Draw in denominator (Fix #5)
        var (homeXg, awayXg) = ApportionXg(totalXg, match.HomeWin, match.Draw, match.AwayWin);

        // 3. Poisson probability of each team scoring at least 1 goal
        var pHomeScores = 1.0 - Math.Exp(-homeXg);
        var pAwayScores = 1.0 - Math.Exp(-awayXg);

        // 4. Independent BTTS probability
        var rawBtts = pHomeScores * pAwayScores;

        // 5. Zero-inflation correction — penalize slightly (Fix #3)
        // 0-0 draws happen more often than pure Poisson predicts
        var balance = 1.0 - Math.Abs(pHomeScores - pAwayScores);
        var btts = rawBtts * (1.05 - (0.10 * balance));

        // No sigmoid calibration — market probabilities are already calibrated (Fix #2)
        var finalProb = Math.Clamp(btts, 0.0, 1.0);
        
        // Self-learning weights using geometric mean (Fix #11)
        var ahWeight = GetHistoricalWeight(accuracies, "BothTeamsScore", 
            ("AhMinusHalfHome", match.AhMinusHalfHome), 
            ("AhMinusOneHome", match.AhMinusOneHome),
            ("HomeWin", match.HomeWin));
            
        var overTwoWeight = GetHistoricalWeight(accuracies, "BothTeamsScore", 
            ("OverTwoGoals", match.OverTwoGoals),
            ("OverThreeGoals", match.OverThreeGoals),
            ("OverOnePointFive", match.OverOnePointFive));
        
        finalProb *= Math.Sqrt(ahWeight * overTwoWeight);

        return Math.Clamp(finalProb, 0.0, 1.0);
    }

    public double CalculateOverTwoGoalsProbability(MatchData match, List<ModelAccuracy> accuracies)
    {
        var trueOver25 = GetTrueOver25(match);
        if (trueOver25 <= 0) return 0;

        var totalXg = EstimateTotalXg(trueOver25, match.OverOnePointFive);

        // Poisson probability of 3+ goals
        var p0 = PoissonProb(0, totalXg);
        var p1 = PoissonProb(1, totalXg);
        var p2 = PoissonProb(2, totalXg);
        var poissonOver25 = 1.0 - (p0 + p1 + p2);

        // Blend market truth with Poisson anchor
        var blended = (trueOver25 * 0.7) + (poissonOver25 * 0.3);

        // No sigmoid (Fix #2) — use blended directly
        var finalProb = Math.Clamp(blended, 0.0, 1.0);
        
        // Self-learning weights using geometric mean (Fix #11)
        var overTwoWeight = GetHistoricalWeight(accuracies, "Over2.5Goals", 
            ("OverTwoGoals", match.OverTwoGoals),
            ("OverThreeGoals", match.OverThreeGoals),
            ("OverOnePointFive", match.OverOnePointFive));
            
        var ahHomeWeight = GetHistoricalWeight(accuracies, "Over2.5Goals", 
            ("AhMinusHalfHome", match.AhMinusHalfHome),
            ("AhMinusOneHome", match.AhMinusOneHome),
            ("HomeWin", match.HomeWin));
        
        finalProb *= Math.Sqrt(overTwoWeight * ahHomeWeight);
        
        return Math.Clamp(finalProb, 0.0, 1.0);
    }

    public double CalculateDrawProbability(MatchData match, List<ModelAccuracy> accuracies)
    {
        if (match.Draw <= 0) return 0;

        var trueOver25 = GetTrueOver25(match);
        var totalXg = EstimateTotalXg(trueOver25, match.OverOnePointFive);

        // Apportion xG including Draw (Fix #5)
        var (homeXg, awayXg) = ApportionXg(totalXg, match.HomeWin, match.Draw, match.AwayWin);

        // Poisson draw probability from score matrix (Fix #4 — consistent method)
        var poissonDraw = CalculateScoreMatrixDraw(homeXg, awayXg);

        // Blend bookie implied Draw with Poisson draw
        var blended = (match.Draw * 0.6) + (poissonDraw * 0.4);

        // No sigmoid (Fix #2)
        var finalProb = Math.Clamp(blended, 0.0, 1.0);
        
        // Self-learning weights using geometric mean (Fix #11)
        var impliedDrawWeight = GetHistoricalWeight(accuracies, "Draw", 
            ("DrawOdds", match.Draw),
            ("HomeWin", match.HomeWin));
            
        var ahHomeWeight = GetHistoricalWeight(accuracies, "Draw", 
            ("AhMinusHalfHome", match.AhMinusHalfHome),
            ("AhPlusHalfAway", match.AhPlusHalfAway));
        
        finalProb *= Math.Sqrt(impliedDrawWeight * ahHomeWeight);

        return Math.Clamp(finalProb, 0.0, 1.0);
    }

    public bool IsStrongHomeWin(MatchData match, List<ModelAccuracy> accuracies)
    {
        if (match.HomeWin < _settings.HomeWinStrong) return false;
        
        var confidence = CalculateHomeWinProbability(match, accuracies);
        return confidence >= 0.68;
    }

    public double CalculateHomeWinProbability(MatchData match, List<ModelAccuracy> accuracies)
    {
        var trueOver25 = GetTrueOver25(match);
        var totalXg = EstimateTotalXg(trueOver25, match.OverOnePointFive);

        if (totalXg < _settings.MinTotalXgRequiredForWin) return 0; 

        // Apportion xG (Fix #5)
        var (homeXg, awayXg) = ApportionXg(totalXg, match.HomeWin, match.Draw, match.AwayWin);

        // Poisson-based win probability from score matrix (Fix #4)
        var poissonHomeWin = CalculateScoreMatrixHomeWin(homeXg, awayXg);

        // Blend 70% market + 30% Poisson (consistent with Over 2.5 pattern)
        var confidence = (match.HomeWin * 0.7) + (poissonHomeWin * 0.3);

        // AH -1 confirms multi-goal supremacy
        if (match.AhMinusOneHome > 0.45)
            confidence *= 1.05;

        // Self-learning weight
        var historyWeight = GetHistoricalWeight(accuracies, "StraightWin", 
            ("AhMinusHalfHome", match.AhMinusHalfHome),
            ("AhMinusOneHome", match.AhMinusOneHome),
            ("HomeWin", match.HomeWin));
            
        confidence *= historyWeight;

        return Math.Clamp(confidence, 0.0, 1.0);
    }

    public bool IsStrongAwayWin(MatchData match, List<ModelAccuracy> accuracies)
    {
        if (match.AwayWin < _settings.AwayWinStrong) return false;

        var confidence = CalculateAwayWinProbability(match, accuracies);
        return confidence >= 0.70;
    }

    public double CalculateAwayWinProbability(MatchData match, List<ModelAccuracy> accuracies)
    {
        var trueOver25 = GetTrueOver25(match);
        var totalXg = EstimateTotalXg(trueOver25, match.OverOnePointFive);

        if (totalXg < _settings.MinTotalXgRequiredForWin) return 0;

        // Apportion xG (Fix #5)
        var (homeXg, awayXg) = ApportionXg(totalXg, match.HomeWin, match.Draw, match.AwayWin);

        // Poisson-based win probability from score matrix (Fix #4)
        var poissonAwayWin = CalculateScoreMatrixAwayWin(homeXg, awayXg);

        // Blend 70% market + 30% Poisson
        var confidence = (match.AwayWin * 0.7) + (poissonAwayWin * 0.3);

        if (match.AhMinusOneAway > 0.45)
            confidence *= 1.05;
            
        // Self-learning weight
        var historyWeight = GetHistoricalWeight(accuracies, "StraightWin", 
            ("AhPlusHalfAway", match.AhPlusHalfAway),
            ("AhPlusHalfHome", match.AhPlusHalfHome),
            ("AwayWin", match.AwayWin));
            
        confidence *= historyWeight;

        return Math.Clamp(confidence, 0.0, 1.0);
    }

    // --- Core Data Science Helpers ---

    /// <summary>
    /// Extracts the true Over 2.5 probability.
    /// </summary>
    private static double GetTrueOver25(MatchData match)
    {
        if (match.OverTwoGoals > 0) return match.OverTwoGoals;

        if (match is { OverThreeGoals: > 0, OverOnePointFive: > 0 })
            return (match.OverOnePointFive + match.OverThreeGoals) / 2.0;

        return 0;
    }

    /// <summary>
    /// Converts a market Over 2.5 probability into Total Expected Goals (xG)
    /// using inverse Poisson CDF via Newton-Raphson iteration (Fix #1).
    /// Finds λ such that P(X > 2 | Poisson(λ)) = over25Prob.
    /// </summary>
    private static double EstimateTotalXg(double over25Prob, double over15Prob)
    {
        if (over25Prob > 0)
            return InversePoissonOver(over25Prob, threshold: 2);
        if (over15Prob > 0)
            return InversePoissonOver(over15Prob, threshold: 1);
        return 2.5; // Global soccer average fallback
    }

    /// <summary>
    /// Newton-Raphson solver: find λ such that P(X > threshold | Poisson(λ)) = targetProb.
    /// </summary>
    private static double InversePoissonOver(double targetProb, int threshold)
    {
        // P(X > threshold) = 1 - CDF(threshold)
        // We want to find λ such that 1 - CDF(threshold, λ) = targetProb
        // i.e., CDF(threshold, λ) = 1 - targetProb

        var targetCdf = 1.0 - targetProb;
        targetCdf = Math.Clamp(targetCdf, 0.01, 0.99);

        // Initial guess from linear approximation
        var lambda = threshold + 1.0 - targetCdf * (threshold + 1.0);
        lambda = Math.Max(lambda, 0.5);

        // Newton-Raphson: f(λ) = CDF(threshold, λ) - targetCdf, find root
        for (var i = 0; i < 20; i++)
        {
            var cdf = 0.0;
            for (var k = 0; k <= threshold; k++)
                cdf += PoissonProb(k, lambda);

            var error = cdf - targetCdf;

            if (Math.Abs(error) < 1e-6)
                break;

            // Derivative of CDF w.r.t. λ: d/dλ CDF = -PoissonProb(threshold, λ) + PoissonProb(threshold, λ)/λ * threshold - PoissonProb(threshold, λ)
            // Simplified: d/dλ Σ P(k,λ) = -P(threshold, λ) (the last term's contribution)
            // More precisely: d/dλ P(k, λ) = P(k-1, λ) - P(k, λ), so d/dλ CDF(n, λ) = -P(n, λ)
            var dCdf = -PoissonProb(threshold, lambda);

            if (Math.Abs(dCdf) < 1e-12)
                break;

            lambda -= error / dCdf;
            lambda = Math.Clamp(lambda, 0.1, 6.0);
        }

        return Math.Clamp(lambda, 0.5, 5.5);
    }

    /// <summary>
    /// Apportions total xG between home and away teams using 1X2 probabilities.
    /// Includes Draw in the denominator to avoid inflating the favourite's xG share (Fix #5).
    /// </summary>
    private static (double homeXg, double awayXg) ApportionXg(double totalXg, double homeWin, double draw, double awayWin)
    {
        var total1x2 = homeWin + draw + awayWin;
        if (total1x2 <= 0) 
            return (totalXg * 0.5, totalXg * 0.5);

        // Calculate xG shares using full 1X2 market
        // The draw portion gets split equally since draws imply balanced scoring
        var homeStrength = homeWin + (draw * 0.5);
        var awayStrength = awayWin + (draw * 0.5);
        var strengthTotal = homeStrength + awayStrength;

        var homeShare = homeStrength / strengthTotal;
        var awayShare = awayStrength / strengthTotal;

        return (totalXg * homeShare, totalXg * awayShare);
    }

    /// <summary>
    /// Builds a bivariate Poisson score matrix (0-5 × 0-5) and returns P(Home > Away).
    /// </summary>
    private static double CalculateScoreMatrixHomeWin(double homeXg, double awayXg)
    {
        double prob = 0;
        for (var h = 0; h <= 5; h++)
            for (var a = 0; a < h; a++)
                prob += PoissonProb(h, homeXg) * PoissonProb(a, awayXg);
        return prob;
    }

    /// <summary>
    /// Builds a bivariate Poisson score matrix and returns P(Away > Home).
    /// </summary>
    private static double CalculateScoreMatrixAwayWin(double homeXg, double awayXg)
    {
        double prob = 0;
        for (var a = 0; a <= 5; a++)
            for (var h = 0; h < a; h++)
                prob += PoissonProb(h, homeXg) * PoissonProb(a, awayXg);
        return prob;
    }

    /// <summary>
    /// Builds a bivariate Poisson score matrix and returns P(Home == Away).
    /// </summary>
    private static double CalculateScoreMatrixDraw(double homeXg, double awayXg)
    {
        double prob = 0;
        for (var k = 0; k <= 5; k++)
            prob += PoissonProb(k, homeXg) * PoissonProb(k, awayXg);
        return prob;
    }

    /// <summary>
    /// Calculates the Poisson Probability of exactly k events occurring.
    /// </summary>
    private static double PoissonProb(int k, double lambda)
    {
        if (lambda <= 0) return k == 0 ? 1.0 : 0.0;
        
        double kFact = 1;
        for (int i = 2; i <= k; i++) kFact *= i;
        
        return (Math.Exp(-lambda) * Math.Pow(lambda, k)) / kFact;
    }
}