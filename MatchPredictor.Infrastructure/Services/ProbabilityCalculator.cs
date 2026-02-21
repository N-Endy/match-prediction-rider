using System;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Infrastructure.Services;

/// <summary>
/// Data-driven probability calculator using bookmaker odds, over/under lines,
/// and Asian Handicap signals. Uses sigmoid squashing for smooth 0-1 outputs
/// and gracefully handles missing data fields.
/// </summary>
public class ProbabilityCalculator : IProbabilityCalculator
{
    public double CalculateBttsProbability(MatchData match)
    {
        // BTTS = both teams score. Requires:
        // 1. Goals expected (high over lines)
        // 2. Balanced match (neither side dominant enough to keep a clean sheet)
        
        var goalSignal = GetGoalExpectation(match);
        var balance = GetMatchBalance(match);
        
        // Base: blend goal expectation with balance
        // BTTS is most likely in high-scoring, balanced matches
        var raw = goalSignal * 0.6 + balance * 0.4;
        
        // AH refinement: if AH_0 lines are close, teams are evenly matched
        if (match is { AhZeroHome: > 0, AhZeroAway: > 0 })
        {
            var ahBalance = 1.0 - Math.Abs(match.AhZeroHome - match.AhZeroAway);
            // Smooth multiplier: balanced → up to 1.2x, lopsided → down to 0.85x
            raw *= 0.85 + 0.35 * ahBalance;
        }
        
        // Over 1.5 confirmation: at least 2 goals expected means BTTS feasible
        if (match.OverOnePointFive > 0)
        {
            if (match.OverOnePointFive > 0.75)
                raw *= 1.10;
            else if (match.OverOnePointFive < 0.55)
                raw *= 0.85;
        }

        // Squash through sigmoid centered at 0.5 for smooth 0-1 output
        return Sigmoid(raw, center: 0.50, steepness: 6.0);
    }

    public double CalculateOverTwoGoalsProbability(MatchData match)
    {
        var baseProb = match.OverTwoGoals;
        
        if (baseProb <= 0)
            baseProb = EstimateOverTwoFromAlternatives(match);
        
        if (baseProb <= 0)
            return 0; // No data to work with
        
        // Curve strength: if o_3.5 is also high, it's a very goal-heavy match
        var curveBoost = 1.0;
        if (match.OverThreeGoals > 0 && match.OverTwoGoals > 0)
        {
            var gradient = match.OverThreeGoals / match.OverTwoGoals;
            // gradient near 0.8+ = extremely goal-heavy; near 0.3 = typical
            curveBoost = 0.9 + 0.2 * gradient; // Range: 0.9x to ~1.1x
        }
        
        var raw = baseProb * curveBoost;
        
        // AH dominance boost: if one team is expected to win by 2+, more goals likely
        if (match.AhMinusOneHome > 0.50)
        {
            raw *= 1.0 + (match.AhMinusOneHome - 0.50) * 0.4; // Up to ~1.2x
        }
        else if (match.AhMinusOneAway > 0.50)
        {
            raw *= 1.0 + (match.AhMinusOneAway - 0.50) * 0.4;
        }
        
        return Math.Clamp(raw, 0, 1.0);
    }
    
    public double CalculateDrawProbability(MatchData match)
    {
        var baseDrawProb = match.Draw;
        if (baseDrawProb <= 0) return 0;
        
        var raw = baseDrawProb;
        
        // AH-derived draw signal: gap between ah_0 (includes draw refund) and ah_-0.5 (no draw)
        if (match is { AhZeroHome: > 0, AhMinusHalfHome: > 0 })
        {
            var drawGap = match.AhZeroHome - match.AhMinusHalfHome;
            
            if (drawGap > 0.10)
                raw *= 1.25; // Strong draw signal
            else if (drawGap > 0.05)
                raw *= 1.12;
            else if (drawGap < 0.02)
                raw *= 0.88; // Clean result expected, not draw
        }
        
        // Balance confirmation from AH_0: draws require closely-matched teams
        if (match is { AhZeroHome: > 0, AhZeroAway: > 0 })
        {
            var ahDiff = Math.Abs(match.AhZeroHome - match.AhZeroAway);
            if (ahDiff < 0.08)
                raw *= 1.15; // Very even
            else if (ahDiff > 0.25)
                raw *= 0.82; // One team clearly dominant
        }
        
        // Under goals confirmation: draws tend to be low-scoring
        if (match.UnderTwoGoals > 0.50)
            raw *= 1.08;
        
        return Math.Clamp(raw, 0, 1.0);
    }
    
    public bool IsStrongHomeWin(MatchData match)
    {
        if (match.HomeWin < PredictionThresholds.HomeWinStrong)
            return false;

        var confidence = match.HomeWin;
        
        // AH confirmation: multiplicative factors instead of additive bonuses
        if (match.AhMinusHalfHome > 0.60)
            confidence *= 1.15; // Strong AH confirmation
        else if (match.AhMinusHalfHome > 0.50)
            confidence *= 1.06;
        
        // AH_-1 boost: home expected to win by 2+ goals
        if (match.AhMinusOneHome > 0.45)
            confidence *= 1.08;
        
        // Goal scoring confirmation
        if (match.OverTwoGoals >= PredictionThresholds.OverGoalsForWin)
            confidence *= 1.04;

        return confidence >= 0.70;
    }
    
    public bool IsStrongAwayWin(MatchData match)
    {
        if (match.AwayWin < PredictionThresholds.AwayWinStrong)
            return false;

        var confidence = match.AwayWin;
        
        // AH confirmation for away win
        if (match.AhPlusHalfAway > 0.60)
            confidence *= 1.15;
        else if (match.AhPlusHalfAway > 0.50)
            confidence *= 1.06;
        
        // When AH_-0.5 home is very low, it confirms away dominance
        if (match.AhMinusHalfHome is > 0 and < 0.35)
            confidence *= 1.08;
        
        if (match.OverTwoGoals >= PredictionThresholds.OverGoalsForWin)
            confidence *= 1.04;

        return confidence >= 0.72;
    }
    
    // --- Helper methods ---
    
    /// <summary>
    /// Estimates expected goals from the over/under probability curve.
    /// Uses ALL non-zero over lines instead of filtering by arbitrary thresholds.
    /// </summary>
    private static double GetGoalExpectation(MatchData match)
    {
        var signals = 0.0;
        var weights = 0.0;
        
        // Use any available non-zero over line — don't filter by arbitrary thresholds
        if (match.OverOnePointFive > 0) { signals += match.OverOnePointFive * 0.15; weights += 0.15; }
        if (match.OverTwoGoals > 0)     { signals += match.OverTwoGoals * 0.35;     weights += 0.35; }
        if (match.OverThreeGoals > 0)   { signals += match.OverThreeGoals * 0.30;   weights += 0.30; }
        if (match.OverFourGoals > 0)    { signals += match.OverFourGoals * 0.20;    weights += 0.20; }
        
        if (weights > 0)
            return signals / weights;
        
        // If no over lines at all, fall back to 1X2: high away+home with low draw → more goals
        if (match.HomeWin > 0 && match.AwayWin > 0)
            return (match.HomeWin + match.AwayWin) * 0.6; // Rough proxy
        
        return 0.4; // Conservative default
    }
    
    /// <summary>
    /// Measures how balanced a match is (0 = one-sided, 1 = perfectly balanced).
    /// </summary>
    private static double GetMatchBalance(MatchData match)
    {
        if (match is { AhZeroHome: > 0, AhZeroAway: > 0 })
            return 1.0 - Math.Abs(match.AhZeroHome - match.AhZeroAway);
        
        // Fallback: use 1X2 balance
        var winDiff = Math.Abs(match.HomeWin - match.AwayWin);
        return Math.Max(0, 1.0 - winDiff * 1.5);
    }
    
    /// <summary>
    /// Estimates Over 2.5 probability from adjacent over/under lines.
    /// </summary>
    private static double EstimateOverTwoFromAlternatives(MatchData match)
    {
        if (match is { OverThreeGoals: > 0, OverOnePointFive: > 0 })
            return (match.OverOnePointFive + match.OverThreeGoals) / 2.0;
        if (match.OverThreeGoals > 0)
            return match.OverThreeGoals * 1.3;
        if (match.UnderTwoGoals > 0)
            return 1.0 - match.UnderTwoGoals;
            
        return 0;
    }
    
    /// <summary>
    /// Logistic sigmoid for smooth probability squashing.
    /// Maps any value to (0, 1) with configurable center and steepness.
    /// </summary>
    private static double Sigmoid(double x, double center, double steepness)
    {
        return 1.0 / (1.0 + Math.Exp(-steepness * (x - center)));
    }
}