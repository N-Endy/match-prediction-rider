using System;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Infrastructure.Services;

/// <summary>
/// Improved probability calculator with data-driven thresholds
/// This version moves away from arbitrary bonus weights and toward calibrated coefficients
/// </summary>
public class ProbabilityCalculator : IProbabilityCalculator
{
    private const double HOME_WIN_THRESHOLD = 0.60;
    private const double AWAY_WIN_THRESHOLD = 0.62;

    public double CalculateBttsProbability(MatchData match)
    {
        // BTTS probability based on total goals and match balance
        var totalGoalProb = (match.OverTwoGoals + match.OverThreeGoals) / 2.0;
        var underGoalPenalty = (match.UnderTwoGoals > 0.60 || match.UnderThreeGoals > 0.70) ? -0.10 : 0;
        var highOverThreeBonus = match.OverThreeGoals > 0.4 && match is { HomeWin: > 0.30, AwayWin: > 0.30 } ? 0.10 : 0;
        
        var winBalance = Math.Abs(match.HomeWin - match.AwayWin);
        var balanceBonus = winBalance < 0.20 ? 0.08 : (winBalance < 0.30 ? 0.04 : 0);
        var competitivenessBonus = match is { HomeWin: > 0.30, AwayWin: > 0.30 } ? 0.05 : 0;
        
        var result = totalGoalProb + balanceBonus + competitivenessBonus + highOverThreeBonus + underGoalPenalty;
        return Math.Min(result, 1.0);
    }

    public double CalculateOverTwoGoalsProbability(MatchData match)
    {
        // Over 2.5 based on direct probabilities and match competitiveness
        var baseScore = match.OverTwoGoals * 0.50 + match.OverThreeGoals * 0.50;
        var competitiveBonus = match is { HomeWin: > 0.35, AwayWin: > 0.30 } ? 0.10 : 0;
        var highOverThreeBonus = match is { OverThreeGoals: > 0.45, OverTwoGoals: 0 } ? 0.10 : 0;
        var heavyFavoriteBonus = (match.HomeWin > 0.50 || match.AwayWin > 0.50) && match.OverTwoGoals > 0.55 ? 0.12 : 0;
        var underTwoNegative = match.UnderTwoGoals > 0.60 ? -0.15 : 0;
        
        var result = baseScore + competitiveBonus + heavyFavoriteBonus + underTwoNegative + highOverThreeBonus;
        return Math.Clamp(result, 0, 1.0);
    }
    
    public double CalculateDrawProbability(MatchData match)
    {
        // Draw probability based on balanced matches and low-scoring expectations
        var baseScore = match.Draw * 0.70;
        
        var winBalance = Math.Abs(match.HomeWin - match.AwayWin);
        var balanceBonus = winBalance < 0.15 ? 0.15 : (winBalance < 0.25 ? 0.08 : 0);
        var underGoalsBonus = (match.UnderTwoGoals > 0.55 || match.UnderThreeGoals > 0.65) ? 0.10 : 0;
        
        var result = baseScore + balanceBonus + underGoalsBonus;
        return Math.Clamp(result, 0, 1.0);
    }
    
    public bool IsStrongHomeWin(MatchData match)
    {
        if (match.HomeWin < HOME_WIN_THRESHOLD)
            return false;

        var score = match.HomeWin;
        
        if (match.OverTwoGoals > 0.60)
            score += 0.08;
        
        if (match.AwayWin < 0.25)
            score += 0.10;
        
        if (match.UnderThreeGoals > 0.70)
            score -= 0.10;
        
        return score >= 0.70;
    }
    
    public bool IsStrongAwayWin(MatchData match)
    {
        if (match.AwayWin < AWAY_WIN_THRESHOLD)
            return false;

        var score = match.AwayWin;
        
        if (match.OverTwoGoals > 0.60)
            score += 0.08;
        
        if (match.HomeWin < 0.25)
            score += 0.12;
        
        if (match.UnderThreeGoals > 0.70)
            score -= 0.12;
        
        return score >= 0.72;
    }
}