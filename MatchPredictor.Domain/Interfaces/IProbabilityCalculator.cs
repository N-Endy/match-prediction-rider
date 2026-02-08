using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IProbabilityCalculator
{
    double CalculateBttsProbability(MatchData match);
    double CalculateOverTwoGoalsProbability(MatchData match);
    double CalculateDrawProbability(MatchData match);
    bool IsStrongHomeWin(MatchData match);
    bool IsStrongAwayWin(MatchData match);
}