using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IProbabilityCalculator
{
    MatchProbabilities CalculateProbabilities(MatchData match);
    double CalculateOverTwoPointFiveSetsProbability(MatchData match);
    double CalculateUnderTwoPointFiveSetsProbability(MatchData match);
    double CalculateHomeWinProbability(MatchData match);
    double CalculateAwayWinProbability(MatchData match);
    double CalculateHomeSetHandicapProbability(MatchData match);
    double CalculateAwaySetHandicapProbability(MatchData match);
}
