using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Utils;

namespace MatchPredictor.Infrastructure.Services;

/// <summary>
/// Data-driven probability calculator using Expected Goals (xG) and Poisson distributions.
/// Designed specifically for pre-processed True Probabilities (margin-free data).
/// Eliminates double-counting of correlated betting lines.
/// </summary>
public class ProbabilityCalculator : IProbabilityCalculator
{
    private readonly PredictionSettings _settings;

    public ProbabilityCalculator(Microsoft.Extensions.Options.IOptions<PredictionSettings> options)
    {
        _settings = options.Value;
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
        var ahWeight = HistoricalWeightCalculator.GetHistoricalWeight(accuracies, "BothTeamsScore", 
            ("AhMinusHalfHome", match.AhMinusHalfHome), 
            ("AhMinusOneHome", match.AhMinusOneHome),
            ("HomeWin", match.HomeWin)); // Correlated fallback chain
            
        var overTwoWeight = HistoricalWeightCalculator.GetHistoricalWeight(accuracies, "BothTeamsScore", 
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
        var overTwoWeight = HistoricalWeightCalculator.GetHistoricalWeight(accuracies, "Over2.5Goals", 
            ("OverTwoGoals", match.OverTwoGoals),
            ("OverThreeGoals", match.OverThreeGoals),
            ("OverOnePointFive", match.OverOnePointFive));
            
        var ahHomeWeight = HistoricalWeightCalculator.GetHistoricalWeight(accuracies, "Over2.5Goals", 
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
        var impliedDrawWeight = HistoricalWeightCalculator.GetHistoricalWeight(accuracies, "Draw", 
            ("DrawOdds", match.Draw), // Try actual draw odds first
            ("HomeWin", match.HomeWin)); // Fallback
            
        var ahHomeWeight = HistoricalWeightCalculator.GetHistoricalWeight(accuracies, "Draw", 
            ("AhMinusHalfHome", match.AhMinusHalfHome),
            ("AhPlusHalfAway", match.AhPlusHalfAway));
        
        finalProb *= (impliedDrawWeight + ahHomeWeight) / 2.0;

        return Math.Clamp(finalProb, 0.0, 1.0);
    }

    public bool IsStrongHomeWin(MatchData match, List<ModelAccuracy> accuracies)
    {
        if (match.HomeWin < _settings.HomeWinStrong) return false;

        var trueOver25 = GetTrueOver25(match);
        var totalXg = EstimateTotalXg(trueOver25, match.OverOnePointFive);

        // Expected goals acts as a variance filter.
        // If a match has low xG (e.g., 1.8), even heavy favorites are at massive risk of a lucky 1-1 draw.
        if (totalXg < _settings.MinTotalXgRequiredForWin) return false; 

        var confidence = match.HomeWin;

        // Only boost if the bookmaker expects a blowout (AH -1 confirms multiple goal supremacy)
        if (match.AhMinusOneHome > 0.45)
            confidence *= 1.05; // Modest boost, avoiding heavy double-counting

        // --- Apply Self-Learning Weights with Fallbacks ---
        var historyWeight = HistoricalWeightCalculator.GetHistoricalWeight(accuracies, "StraightWin", 
            ("AhMinusHalfHome", match.AhMinusHalfHome),
            ("AhMinusOneHome", match.AhMinusOneHome),
            ("HomeWin", match.HomeWin));
            
        confidence *= historyWeight;

        return confidence >= 0.68; // Matches your empirical 80th percentile
    }

    public bool IsStrongAwayWin(MatchData match, List<ModelAccuracy> accuracies)
    {
        if (match.AwayWin < _settings.AwayWinStrong) return false;

        var trueOver25 = GetTrueOver25(match);
        var totalXg = EstimateTotalXg(trueOver25, match.OverOnePointFive);

        // Variance filter: Away favorites in low-scoring games are highly dangerous to bet on
        if (totalXg < _settings.MinTotalXgRequiredForWin) return false;

        var confidence = match.AwayWin;

        if (match.AhMinusOneAway > 0.45)
            confidence *= 1.05;
            
        // --- Apply Self-Learning Weights with Fallbacks ---
        var historyWeight = HistoricalWeightCalculator.GetHistoricalWeight(accuracies, "StraightWin", 
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