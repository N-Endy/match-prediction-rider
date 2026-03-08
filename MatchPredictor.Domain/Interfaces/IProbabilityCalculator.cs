using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IProbabilityCalculator
{
    double CalculateBttsProbability(MatchData match, List<ModelAccuracy> accuracies);
    double CalculateOverTwoGoalsProbability(MatchData match, List<ModelAccuracy> accuracies);
    double CalculateDrawProbability(MatchData match, List<ModelAccuracy> accuracies);
    bool IsStrongHomeWin(MatchData match, List<ModelAccuracy> accuracies);
    bool IsStrongAwayWin(MatchData match, List<ModelAccuracy> accuracies);
    double CalculateHomeWinProbability(MatchData match, List<ModelAccuracy> accuracies);
    double CalculateAwayWinProbability(MatchData match, List<ModelAccuracy> accuracies);
}