// using MatchPredictor.Domain.Interfaces;
// using MatchPredictor.Domain.Models;
//
// namespace MatchPredictor.Infrastructure.Services;
//
// /// <summary>
// /// Data-driven probability calculator using bookmaker odds, over/under lines,
// /// and Asian Handicap signals. Uses sigmoid squashing for smooth 0-1 outputs
// /// and gracefully handles missing data fields.
// /// </summary>
// public class ProbabilityCalculator : IProbabilityCalculator
// {
//     public double CalculateBttsProbability(MatchData match)
//     {
//         // BTTS = both teams score. Requires:
//         // 1. Goals expected (high over lines)
//         // 2. Balanced match (neither side dominant enough to keep a clean sheet)
//
//         var goalSignal = GetGoalExpectation(match);
//         var balance = GetMatchBalance(match);
//
//         // Base: blend goal expectation with balance
//         // BTTS is most likely in high-scoring, balanced matches
//         var raw = goalSignal * 0.6 + balance * 0.4;
//
//         // AH refinement: if AH_0 lines are close, teams are evenly matched
//         if (match is { AhZeroHome: > 0, AhZeroAway: > 0 })
//         {
//             var ahBalance = 1.0 - Math.Abs(match.AhZeroHome - match.AhZeroAway);
//
//             // Tuned: narrower multiplier range (reduces overconfidence)
//             // Balanced → mild boost; lopsided → mild penalty
//             raw *= 0.765 + 0.195 * ahBalance;
//         }
//
//         // Over 1.5 confirmation: at least 2 goals expected means BTTS feasible
//         if (match.OverOnePointFive > 0)
//         {
//             switch (match.OverOnePointFive)
//             {
//                 // Tuned thresholds: avoid overly aggressive gating
//                 case > 0.66:
//                     raw *= 1.13;
//                     break;
//                 case < 0.63:
//                     raw *= 0.82;
//                     break;
//             }
//         }
//
//         // Tuned: softer + re-centered sigmoid to improve calibration
//         return Sigmoid(raw, center: 0.575, steepness: 3.94);
//     }
//
//     public double CalculateOverTwoGoalsProbability(MatchData match)
//     {
//         var baseProb = match.OverTwoGoals;
//
//         if (baseProb <= 0)
//             baseProb = EstimateOverTwoFromAlternatives(match);
//
//         if (baseProb <= 0)
//             return 0; // No data to work with
//
//         // Curve strength: if o_3.5 is also high, it's a very goal-heavy match
//         var curveBoost = 1.0;
//         if (match is { OverThreeGoals: > 0, OverTwoGoals: > 0 })
//         {
//             var gradient = match.OverThreeGoals / match.OverTwoGoals;
//             // gradient near 0.8+ = extremely goal-heavy; near 0.3 = typical
//             curveBoost = 0.9 + 0.2 * gradient; // Range: 0.9x to ~1.1x
//         }
//
//         var raw = baseProb * curveBoost;
//
//         // AH dominance boost: if one team is expected to win by 2+, more goals likely
//         if (match.AhMinusOneHome > 0.50)
//         {
//             raw *= 1.0 + (match.AhMinusOneHome - 0.50) * 0.4; // Up to ~1.2x
//         }
//         else if (match.AhMinusOneAway > 0.50)
//         {
//             raw *= 1.0 + (match.AhMinusOneAway - 0.50) * 0.4;
//         }
//
//         // Previously: Math.Clamp(raw, 0, 1.0)
//         // Tuned: calibration sigmoid improves log-loss/Brier while keeping ranking similar.
//         return Calibrate(raw, center: 0.638, steepness: 3.27);
//     }
//
//     public double CalculateDrawProbability(MatchData match)
//     {
//         var baseDrawProb = match.Draw;
//         if (baseDrawProb <= 0) return 0;
//
//         var raw = baseDrawProb;
//
//         // AH-derived draw signal: gap between ah_0 (includes draw refund) and ah_-0.5 (no draw)
//         if (match is { AhZeroHome: > 0, AhMinusHalfHome: > 0 })
//         {
//             var drawGap = match.AhZeroHome - match.AhMinusHalfHome;
//
//             switch (drawGap)
//             {
//                 case > 0.10 when baseDrawProb > 0.30:
//                     raw *= 1.25; // Strong draw signal
//                     break;
//                 case > 0.05:
//                     raw *= 1.12;
//                     break;
//                 case < 0.02:
//                     raw *= 0.88; // Clean result expected, not draw
//                     break;
//             }
//         }
//
//         // Balance confirmation from AH_0: draws require closely-matched teams
//         if (match is { AhZeroHome: > 0, AhZeroAway: > 0 })
//         {
//             var ahDiff = Math.Abs(match.AhZeroHome - match.AhZeroAway);
//             switch (ahDiff)
//             {
//                 case < 0.08:
//                     raw *= 1.15; // Very even
//                     break;
//                 case > 0.25:
//                     raw *= 0.82; // One team clearly dominant
//                     break;
//             }
//         }
//
//         // Under goals confirmation: draws tend to be low-scoring
//         if (match.UnderTwoGoals > 0.50)
//             raw *= 1.08;
//
//         // Previously: Math.Clamp(raw, 0, 1.0)
//         // Tuned: light calibration (draw probabilities are typically low and sensitive to overconfidence)
//         return Calibrate(raw, center: 0.449, steepness: 3.52);
//     }
//
//     public bool IsStrongHomeWin(MatchData match)
//     {
//         if (match.HomeWin < PredictionThresholds.HomeWinStrong)
//             return false;
//
//         var confidence = match.HomeWin;
//
//         // AH confirmation: multiplicative factors instead of additive bonuses
//         if (match.AhMinusHalfHome > 0.60)
//             confidence *= 1.15; // Strong AH confirmation
//         else if (match.AhMinusHalfHome > 0.50)
//             confidence *= 1.06;
//
//         // AH_-1 boost: home expected to win by 2+ goals
//         if (match.AhMinusOneHome > 0.45)
//             confidence *= 1.08;
//
//         // Goal scoring confirmation
//         if (match.OverTwoGoals >= PredictionThresholds.OverGoalsForWin)
//             confidence *= 1.04;
//
//         return confidence >= 0.70;
//     }
//
//     public bool IsStrongAwayWin(MatchData match)
//     {
//         if (match.AwayWin < PredictionThresholds.AwayWinStrong)
//             return false;
//
//         var confidence = match.AwayWin;
//
//         // AH confirmation for away win
//         if (match.AhPlusHalfAway > 0.60)
//             confidence *= 1.15;
//         else if (match.AhPlusHalfAway > 0.50)
//             confidence *= 1.06;
//
//         // When AH_-0.5 home is very low, it confirms away dominance
//         if (match.AhMinusHalfHome is > 0 and < 0.35)
//             confidence *= 1.08;
//
//         if (match.OverTwoGoals >= PredictionThresholds.OverGoalsForWin)
//             confidence *= 1.04;
//
//         return confidence >= 0.72;
//     }
//
//     // --- Helper methods ---
//
//     /// <summary>
//     /// Calibration wrapper: clamps to [0,1] then applies a sigmoid to smooth confidence.
//     /// This improves probability calibration (log-loss/Brier/ECE) without changing the rest of the pipeline.
//     /// </summary>
//     private static double Calibrate(double p, double center, double steepness)
//     {
//         p = Math.Clamp(p, 0.0, 1.0);
//         return Sigmoid(p, center: center, steepness: steepness);
//     }
//
//     /// <summary>
//     /// Estimates expected goals from the over/under probability curve.
//     /// Uses ALL non-zero over lines instead of filtering by arbitrary thresholds.
//     /// </summary>
//     private static double GetGoalExpectation(MatchData match)
//     {
//         var signals = 0.0;
//         var weights = 0.0;
//
//         // Use any available non-zero over line — don't filter by arbitrary thresholds
//         if (match.OverOnePointFive > 0) { signals += match.OverOnePointFive * 0.15; weights += 0.15; }
//         if (match.OverTwoGoals > 0)     { signals += match.OverTwoGoals * 0.35;     weights += 0.35; }
//         if (match.OverThreeGoals > 0)   { signals += match.OverThreeGoals * 0.30;   weights += 0.30; }
//         if (match.OverFourGoals > 0)    { signals += match.OverFourGoals * 0.20;    weights += 0.20; }
//
//         if (weights > 0)
//             return signals / weights;
//
//         // If no over lines at all, fall back to 1X2: high away+home with low draw → more goals
//         if (match is { HomeWin: > 0, AwayWin: > 0 })
//             return (match.HomeWin + match.AwayWin) * 0.6; // Rough proxy
//
//         return 0.4; // Conservative default
//     }
//
//     /// <summary>
//     /// Measures how balanced a match is (0 = one-sided, 1 = perfectly balanced).
//     /// </summary>
//     private static double GetMatchBalance(MatchData match)
//     {
//         if (match is { AhZeroHome: > 0, AhZeroAway: > 0 })
//             return 1.0 - Math.Abs(match.AhZeroHome - match.AhZeroAway);
//
//         // Fallback: use 1X2 balance
//         var winDiff = Math.Abs(match.HomeWin - match.AwayWin);
//         return Math.Max(0, 1.0 - winDiff * 1.5);
//     }
//
//     /// <summary>
//     /// Estimates Over 2.5 probability from adjacent over/under lines.
//     /// </summary>
//     private static double EstimateOverTwoFromAlternatives(MatchData match)
//     {
//         if (match is { OverThreeGoals: > 0, OverOnePointFive: > 0 })
//             return (match.OverOnePointFive + match.OverThreeGoals) / 2.0;
//         if (match.OverThreeGoals > 0)
//             return match.OverThreeGoals * 1.3;
//         if (match.UnderTwoGoals > 0)
//             return 1.0 - match.UnderTwoGoals;
//
//         return 0;
//     }
//
//     /// <summary>
//     /// Logistic sigmoid for smooth probability squashing.
//     /// Maps any value to (0, 1) with configurable center and steepness.
//     /// </summary>
//     private static double Sigmoid(double x, double center, double steepness)
//     {
//         return 1.0 / (1.0 + Math.Exp(-steepness * (x - center)));
//     }
// }




// Without De-Vig. Recalculated Probabilities based purely on the market's true lines and mathematical Poisson projections.
// This version is more transparent and relies on the market's own signals without attempting to adjust for the bookmaker's margin.

using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Infrastructure.Services;

/// <summary>
/// Data-driven probability calculator using Expected Goals (xG) and Poisson distributions.
/// Designed specifically for pre-processed True Probabilities (margin-free data).
/// Eliminates double-counting of correlated betting lines.
/// </summary>
public class ProbabilityCalculator : IProbabilityCalculator
{
    private double GetHistoricalWeight(List<ModelAccuracy> accuracies, string category, params (string MetricName, double MetricValue)[] fallbacks)
    {
        if (accuracies == null || accuracies.Count == 0 || fallbacks == null || fallbacks.Length == 0) return 1.0;

        foreach (var (metricName, metricValue) in fallbacks)
        {
            // Skip missing data point
            if (metricValue <= 0) continue;

            var profile = accuracies.FirstOrDefault(a => 
                a.Category == category && 
                a.MetricName == metricName && 
                metricValue >= a.MetricRangeStart && 
                metricValue < a.MetricRangeEnd);

            // If we have statistical significance, use it and break the fallback chain
            if (profile != null && profile.TotalPredictions >= 5)
            {
                // If historical accuracy is very poor (< 40%), penalize the probability. 
                // If it's exceptionally good (> 60%), boost the probability.
                var weight = 1.0 + (profile.AccuracyPercentage - 0.50);
                return Math.Clamp(weight, 0.7, 1.3); // Safe guardrails 
            }
        }

        // If all fallbacks were missing or lacked statistical significance, return neutral weight
        return 1.0;
    }

    public double CalculateBttsProbability(MatchData match, List<ModelAccuracy> accuracies)
    {
        var trueOver25 = GetTrueOver25(match);

        // Missing critical data to form a baseline
        if (trueOver25 <= 0 || match.HomeWin <= 0) return 0.0;  

        // 1. Derive Implied Expected Goals (Total xG)
        var totalXg = EstimateTotalXg(trueOver25, match.OverOnePointFive);

        // 2. Apportion xG to Teams based on 1X2 supremacy
        var homeShare = (match.HomeWin + match.AwayWin > 0) 
            ? (match.HomeWin / (match.HomeWin + match.AwayWin)) 
            : 0.5;
            
        var awayShare = 1.0 - homeShare;

        var homeXg = totalXg * homeShare;
        var awayXg = totalXg * awayShare;

        // 3. Calculate Poisson probability of each team scoring at least 1 goal
        var pHomeScores = 1.0 - Math.Exp(-homeXg);
        var pAwayScores = 1.0 - Math.Exp(-awayXg);

        // 4. Independent BTTS probability
        var rawBtts = pHomeScores * pAwayScores;

        // 5. Adjust for zero-inflation (0-0 draws happen slightly more often than pure Poisson predicts)
        var balance = 1.0 - Math.Abs(homeShare - awayShare);
        var btts = rawBtts * (0.95 + (0.10 * balance)); // Slight boost for highly balanced matches

        var finalProb = Calibrate(btts, center: 0.50, steepness: 4.5);
        
        // --- Apply Self-Learning Weights with Fallbacks ---
        var ahWeight = GetHistoricalWeight(accuracies, "BothTeamsScore", 
            ("AhMinusHalfHome", match.AhMinusHalfHome), 
            ("AhMinusOneHome", match.AhMinusOneHome),
            ("HomeWin", match.HomeWin)); // Correlated fallback chain
            
        var overTwoWeight = GetHistoricalWeight(accuracies, "BothTeamsScore", 
            ("OverTwoGoals", match.OverTwoGoals),
            ("OverThreeGoals", match.OverThreeGoals),
            ("OverOnePointFive", match.OverOnePointFive));
        
        finalProb *= (ahWeight + overTwoWeight) / 2.0;

        return Math.Clamp(finalProb, 0.0, 1.0);
    }

    public double CalculateOverTwoGoalsProbability(MatchData match, List<ModelAccuracy> accuracies)
    {
        var trueOver25 = GetTrueOver25(match);
        if (trueOver25 <= 0) return 0;

        var totalXg = EstimateTotalXg(trueOver25, match.OverOnePointFive);

        // Calculate independent Poisson probability of 3 or more goals
        var p0 = PoissonProb(0, totalXg);
        var p1 = PoissonProb(1, totalXg);
        var p2 = PoissonProb(2, totalXg);
        var poissonOver25 = 1.0 - (p0 + p1 + p2);

        // Blend the market's true line with our mathematical Poisson projection.
        // If the market is heavily skewed, Poisson acts as a mathematical anchor to prevent overconfidence.
        var blended = (trueOver25 * 0.7) + (poissonOver25 * 0.3);

        var finalProb = Calibrate(blended, center: 0.55, steepness: 4.5);
        
        // --- Apply Self-Learning Weights with Fallbacks ---
        var overTwoWeight = GetHistoricalWeight(accuracies, "Over2.5Goals", 
            ("OverTwoGoals", match.OverTwoGoals),
            ("OverThreeGoals", match.OverThreeGoals),
            ("OverOnePointFive", match.OverOnePointFive));
            
        var ahHomeWeight = GetHistoricalWeight(accuracies, "Over2.5Goals", 
            ("AhMinusHalfHome", match.AhMinusHalfHome),
            ("AhMinusOneHome", match.AhMinusOneHome),
            ("HomeWin", match.HomeWin));
        
        finalProb *= (overTwoWeight + ahHomeWeight) / 2.0;
        
        return Math.Clamp(finalProb, 0.0, 1.0);
    }

    public double CalculateDrawProbability(MatchData match, List<ModelAccuracy> accuracies)
    {
        if (match.Draw <= 0) return 0;

        var trueOver25 = GetTrueOver25(match);
        var totalXg = EstimateTotalXg(trueOver25, match.OverOnePointFive);

        var homeShare = (match.HomeWin + match.AwayWin > 0) 
            ? (match.HomeWin / (match.HomeWin + match.AwayWin)) 
            : 0.5;
            
        var awayShare = 1.0 - homeShare;

        var homeXg = totalXg * homeShare;
        var awayXg = totalXg * awayShare;

        // Calculate theoretical Poisson draw probability: P(0-0) + P(1-1) + P(2-2) + P(3-3)
        var p00 = PoissonProb(0, homeXg) * PoissonProb(0, awayXg);
        var p11 = PoissonProb(1, homeXg) * PoissonProb(1, awayXg);
        var p22 = PoissonProb(2, homeXg) * PoissonProb(2, awayXg);
        var p33 = PoissonProb(3, homeXg) * PoissonProb(3, awayXg);
        var poissonDraw = p00 + p11 + p22 + p33;

        // Blend bookie implied true draw with our derived poisson draw
        var blended = (match.Draw * 0.6) + (poissonDraw * 0.4);

        var finalProb = Calibrate(blended, center: 0.25, steepness: 5.0);
        
        // --- Apply Self-Learning Weights with Fallbacks ---
        var impliedDrawWeight = GetHistoricalWeight(accuracies, "Draw", 
            ("DrawOdds", match.Draw), // Try actual draw odds first
            ("HomeWin", match.HomeWin)); // Fallback
            
        var ahHomeWeight = GetHistoricalWeight(accuracies, "Draw", 
            ("AhMinusHalfHome", match.AhMinusHalfHome),
            ("AhPlusHalfAway", match.AhPlusHalfAway));
        
        finalProb *= (impliedDrawWeight + ahHomeWeight) / 2.0;

        return Math.Clamp(finalProb, 0.0, 1.0);
    }

    public bool IsStrongHomeWin(MatchData match, List<ModelAccuracy> accuracies)
    {
        if (match.HomeWin < PredictionThresholds.HomeWinStrong) return false;

        var trueOver25 = GetTrueOver25(match);
        var totalXg = EstimateTotalXg(trueOver25, match.OverOnePointFive);

        // Expected goals acts as a variance filter.
        // If a match has low xG (e.g., 1.8), even heavy favorites are at massive risk of a lucky 1-1 draw.
        if (totalXg < 2.0) return false; 

        var confidence = match.HomeWin;

        // Only boost if the bookmaker expects a blowout (AH -1 confirms multiple goal supremacy)
        if (match.AhMinusOneHome > 0.45)
            confidence *= 1.05; // Modest boost, avoiding heavy double-counting

        // --- Apply Self-Learning Weights with Fallbacks ---
        var historyWeight = GetHistoricalWeight(accuracies, "StraightWin", 
            ("AhMinusHalfHome", match.AhMinusHalfHome),
            ("AhMinusOneHome", match.AhMinusOneHome),
            ("HomeWin", match.HomeWin));
            
        confidence *= historyWeight;

        return confidence >= 0.68; // Matches your empirical 80th percentile
    }

    public bool IsStrongAwayWin(MatchData match, List<ModelAccuracy> accuracies)
    {
        if (match.AwayWin < PredictionThresholds.AwayWinStrong) return false;

        var trueOver25 = GetTrueOver25(match);
        var totalXg = EstimateTotalXg(trueOver25, match.OverOnePointFive);

        // Variance filter: Away favorites in low-scoring games are highly dangerous to bet on
        if (totalXg < 2.0) return false;

        var confidence = match.AwayWin;

        if (match.AhMinusOneAway > 0.45)
            confidence *= 1.05;
            
        // --- Apply Self-Learning Weights with Fallbacks ---
        var historyWeight = GetHistoricalWeight(accuracies, "StraightWin", 
            ("AhPlusHalfAway", match.AhPlusHalfAway),
            ("AhPlusHalfHome", match.AhPlusHalfHome),
            ("AwayWin", match.AwayWin));
            
        confidence *= historyWeight;

        return confidence >= 0.70; // Higher threshold for away teams
    }

    // --- Core Data Science Helpers ---

    /// <summary>
    /// Extracts the true Over 2.5 probability. 
    /// No de-vigging required as the dataset provides pure implied probabilities.
    /// </summary>
    private static double GetTrueOver25(MatchData match)
    {
        if (match.OverTwoGoals > 0) return match.OverTwoGoals;

        // Fallback using alternative lines if the main line is missing
        if (match is { OverThreeGoals: > 0, OverOnePointFive: > 0 })
            return (match.OverOnePointFive + match.OverThreeGoals) / 2.0;

        return 0;
    }

    /// <summary>
    /// Converts a market Over 2.5 probability into an absolute Total Expected Goals (xG) figure.
    /// Uses an empirical linear mapping for global soccer averages.
    /// </summary>
    private static double EstimateTotalXg(double over25Prob, double over15Prob)
    {
        if (over25Prob > 0) return 1.0 + (over25Prob * 3.5); // e.g., 50% O2.5 = ~2.75 xG
        if (over15Prob > 0) return 0.5 + (over15Prob * 3.0);
        return 2.5; // Standard global soccer average fallback
    }

    /// <summary>
    /// Calculates the Poisson Probability of exactly k events (goals) occurring.
    /// </summary>
    private static double PoissonProb(int k, double lambda)
    {
        if (lambda <= 0) return k == 0 ? 1.0 : 0.0;
        
        double kFact = 1;
        for (int i = 2; i <= k; i++) kFact *= i;
        
        return (Math.Exp(-lambda) * Math.Pow(lambda, k)) / kFact;
    }

    /// <summary>
    /// Logistic sigmoid for smooth probability squashing.
    /// Maps any value to (0, 1) with configurable center and steepness.
    /// </summary>
    private static double Calibrate(double p, double center, double steepness)
    {
        p = Math.Clamp(p, 0.0, 1.0);
        return 1.0 / (1.0 + Math.Exp(-steepness * (p - center)));
    }
}