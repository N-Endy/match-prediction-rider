using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IProbabilityCalculator
{
    MatchProbabilities CalculateProbabilities(MatchData match);
    double CalculateBttsProbability(MatchData match);
    double CalculateOverTwoGoalsProbability(MatchData match);
    double CalculateUnderTwoGoalsProbability(MatchData match);
    double CalculateHomeWinProbability(MatchData match);
    double CalculateAwayWinProbability(MatchData match);
}
