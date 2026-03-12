using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IProbabilityCalculator
{
    double CalculateBttsProbability(MatchData match);
    double CalculateOverTwoGoalsProbability(MatchData match);
    double CalculateDrawProbability(MatchData match);
    double CalculateHomeWinProbability(MatchData match);
    double CalculateAwayWinProbability(MatchData match);
}
