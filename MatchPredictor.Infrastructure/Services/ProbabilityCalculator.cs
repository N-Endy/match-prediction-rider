using System;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Infrastructure.Services;

/// <summary>
/// Enhanced probability calculator using Asian Handicap data as implied goal
/// expectation signals, combined with multiplicative scoring instead of
/// additive bonuses.
/// 
/// Key insight: AH lines from bookmakers encode the expected goal difference
/// far more accurately than raw 1X2 probabilities. For example:
/// - ah_0_h = 0.60 means the market expects Home to win ~60% when draw is void
/// - ah_-0.5_h = 0.55 means Home wins outright ~55% of the time
/// - The gap between ah_0 and ah_-0.5 reveals how much draw probability exists
/// </summary>
public class ProbabilityCalculator : IProbabilityCalculator
{
    public double CalculateBttsProbability(MatchData match)
    {
        // BTTS requires both teams to score. Best indicators:
        // 1. High Over 2.5 probability (more goals = more likely both score)
        // 2. Balanced match (close AH lines = neither team dominant)
        // 3. High Over 1.5 (at least 2 goals expected)
        // 4. Low Under 1.5 (confirms goals expected)
        
        var goalExpectation = GetGoalExpectation(match);
        var matchBalance = GetMatchBalance(match);
        
        // Multiplicative base: goal expectation × match balance
        // High-scoring balanced matches are the best BTTS candidates
        var score = goalExpectation * matchBalance;
        
        // AH boost: if ah_0 lines are close (0.45-0.55 range), teams are evenly matched
        if (match.AhZeroHome > 0 && match.AhZeroAway > 0)
        {
            var ahBalance = 1.0 - Math.Abs(match.AhZeroHome - match.AhZeroAway);
            score *= (0.7 + 0.6 * ahBalance); // Range: 0.7x to 1.3x multiplier
        }
        
        // Over 1.5 confirmation: if high, both teams likely contributing
        if (match.OverOnePointFive > 0.75)
            score *= 1.15;
        else if (match.OverOnePointFive > 0 && match.OverOnePointFive < 0.55)
            score *= 0.8; // Penalty for low-scoring expectation
        
        return Math.Min(score, 1.0);
    }

    public double CalculateOverTwoGoalsProbability(MatchData match)
    {
        // Over 2.5 uses the granular over/under curve:
        // If o_2.5 is high AND o_3.5 is also decent, it's a strong signal
        // AH data adds: if ah_-1_h is high, home expected to win by 2+, meaning more goals
        
        var baseProb = match.OverTwoGoals;
        
        if (baseProb <= 0)
        {
            // Fallback when o_2.5 is missing: estimate from other lines
            baseProb = EstimateOverTwoFromAlternatives(match);
        }
        
        // Gradient confirmation from the over curve
        var curveStrength = GetOverUnderCurveStrength(match);
        
        // Weighted combination: 60% direct probability, 40% curve-confirmed
        var score = baseProb * 0.6 + curveStrength * 0.4;
        
        // AH boost for dominant matches: if one team is heavily favored on AH,
        // they're expected to score multiple goals
        if (match.AhMinusOneHome > 0.50 || match.AhMinusOneAway > 0)
        {
            var dominance = Math.Max(
                match.AhMinusOneHome > 0 ? match.AhMinusOneHome : 0,
                // If away AH data available via the +0.5 line being very high for away
                match.AhMinusHalfHome > 0 ? 1.0 - match.AhMinusHalfHome : 0
            );
            if (dominance > 0.45)
                score *= 1.0 + (dominance - 0.45) * 0.5; // Up to ~1.25x boost
        }
        
        return Math.Min(score, 1.0);
    }
    
    public double CalculateDrawProbability(MatchData match)
    {
        // Draw detection using AH: the gap between ah_0 and ah_-0.5 reveals draw probability
        // If ah_0_h ≈ ah_-0.5_h, there's very little draw probability (team wins or loses cleanly)
        // If ah_0_h >> ah_-0.5_h, there's significant draw probability
        
        var baseDrawProb = match.Draw;
        var score = baseDrawProb;
        
        // AH-derived draw signal
        if (match.AhZeroHome > 0 && match.AhMinusHalfHome > 0)
        {
            // The "draw gap": probability mass that sits on the draw
            // ah_0 includes draws (home gets refund on draw), ah_-0.5 doesn't
            var drawGap = match.AhZeroHome - match.AhMinusHalfHome;
            
            if (drawGap > 0.10)
                score *= 1.3; // Strong draw signal from AH market
            else if (drawGap > 0.05)
                score *= 1.15;
            else if (drawGap < 0.02)
                score *= 0.85; // AH market says clean result, not a draw
        }
        
        // Balance confirmation: draws happen when teams are closely matched
        if (match.AhZeroHome > 0 && match.AhZeroAway > 0)
        {
            var ahDiff = Math.Abs(match.AhZeroHome - match.AhZeroAway);
            if (ahDiff < 0.08)
                score *= 1.2; // Very evenly matched
            else if (ahDiff > 0.25)
                score *= 0.8; // One team clearly dominant
        }
        
        // Under goals confirmation: draws tend to be low-scoring
        if (match.UnderTwoGoals > 0.50)
            score *= 1.1;
        
        return Math.Min(score, 1.0);
    }
    
    public bool IsStrongHomeWin(MatchData match)
    {
        if (match.HomeWin < PredictionThresholds.HomeWinStrong)
            return false;

        var score = match.HomeWin;
        
        // AH confirmation: if ah_-0.5_h is high, market expects home to win outright
        if (match.AhMinusHalfHome > 0.60)
            score += 0.15; // Strong AH confirmation
        else if (match.AhMinusHalfHome > 0.50)
            score += 0.08;
        
        // ah_-1 boost: if home expected to win by 2+, very strong
        if (match.AhMinusOneHome > 0.45)
            score += 0.10;
        
        // Goal scoring ability confirmation
        if (match.OverTwoGoals >= PredictionThresholds.OverGoalsForWin)
            score += 0.05;

        return score >= 0.7;
    }
    
    public bool IsStrongAwayWin(MatchData match)
    {
        if (match.AwayWin < PredictionThresholds.AwayWinStrong)
            return false;

        var score = match.AwayWin;
        
        // AH confirmation for away: ah_+0.5_a being high means away expected to win
        // (away doesn't need the +0.5 handicap cushion)
        if (match.AhPlusHalfAway > 0.60)
            score += 0.15;
        else if (match.AhPlusHalfAway > 0.50)
            score += 0.08;
        
        // When ah_-0.5_h is very low, it confirms away dominance
        if (match.AhMinusHalfHome > 0 && match.AhMinusHalfHome < 0.35)
            score += 0.10;
        
        if (match.OverTwoGoals >= PredictionThresholds.OverGoalsForWin)
            score += 0.05;

        return score >= 0.72;
    }
    
    // --- Helper methods ---
    
    /// <summary>
    /// Estimates expected goals from the over/under probability curve.
    /// Higher values = more goals expected.
    /// </summary>
    private static double GetGoalExpectation(MatchData match)
    {
        // Weighted average of over lines gives goal expectation signal
        var signals = 0.0;
        var weights = 0.0;
        
        if (match.OverOnePointFive > 0) { signals += match.OverOnePointFive * 0.15; weights += 0.15; }
        if (match.OverTwoGoals > 0)     { signals += match.OverTwoGoals * 0.35;     weights += 0.35; }
        if (match.OverThreeGoals > 0)   { signals += match.OverThreeGoals * 0.30;   weights += 0.30; }
        if (match.OverFourGoals > 0)    { signals += match.OverFourGoals * 0.20;    weights += 0.20; }
        
        return weights > 0 ? signals / weights : (match.OverTwoGoals + match.OverThreeGoals) / 2.0;
    }
    
    /// <summary>
    /// Measures how balanced a match is (0 = one-sided, 1 = perfectly balanced).
    /// Uses AH data when available, falls back to 1X2.
    /// </summary>
    private static double GetMatchBalance(MatchData match)
    {
        if (match.AhZeroHome > 0 && match.AhZeroAway > 0)
        {
            // AH balance: 1 - |home - away|, range [0, 1]
            return 1.0 - Math.Abs(match.AhZeroHome - match.AhZeroAway);
        }
        
        // Fallback: use 1X2 balance
        var winDiff = Math.Abs(match.HomeWin - match.AwayWin);
        return Math.Max(0, 1.0 - winDiff * 2);
    }
    
    /// <summary>
    /// Measures the strength of the over/under curve.
    /// A steep curve (o_2.5 high + o_3.5 also high) = very goal-heavy match.
    /// </summary>
    private static double GetOverUnderCurveStrength(MatchData match)
    {
        if (match.OverTwoGoals <= 0) return 0;
        
        // If o_3.5 is also high relative to o_2.5, the curve is "flat" = lots of goals expected
        var curveGradient = match.OverThreeGoals > 0 
            ? match.OverThreeGoals / match.OverTwoGoals 
            : 0.5;
        
        // curveGradient near 1.0 = nearly as likely to have 4+ goals as 3+
        // curveGradient near 0.5 = typical drop-off
        return match.OverTwoGoals * (0.5 + 0.5 * curveGradient);
    }
    
    /// <summary>
    /// Estimates Over 2.5 probability when the direct column is missing,
    /// using adjacent over/under lines.
    /// </summary>
    private static double EstimateOverTwoFromAlternatives(MatchData match)
    {
        if (match.OverThreeGoals > 0 && match.OverOnePointFive > 0)
        {
            // Interpolate between o_1.5 and o_3.5
            return (match.OverOnePointFive + match.OverThreeGoals) / 2.0;
        }
        if (match.OverThreeGoals > 0)
            return match.OverThreeGoals * 1.3; // Rough upward estimate
        if (match.UnderTwoGoals > 0)
            return 1.0 - match.UnderTwoGoals;
            
        return 0;
    }
}