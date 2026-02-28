using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Infrastructure.Services;

public class DataAnalyzerService : IDataAnalyzerService
{
    private readonly IProbabilityCalculator _probabilityCalculator;

    public DataAnalyzerService(IProbabilityCalculator probabilityCalculator)
    {
        _probabilityCalculator = probabilityCalculator;
    }

    public IEnumerable<MatchData> BothTeamsScore(IEnumerable<MatchData> matches, List<ModelAccuracy> accuracies) =>
        matches.Where(m =>
            _probabilityCalculator.CalculateBttsProbability(m, accuracies) >= PredictionThresholds.BttsScoreThreshold);

    public IEnumerable<MatchData> OverTwoGoals(IEnumerable<MatchData> matches, List<ModelAccuracy> accuracies) =>
        matches.Where(m =>
            _probabilityCalculator.CalculateOverTwoGoalsProbability(m, accuracies) >= PredictionThresholds.OverTwoGoalsStrongThreshold);

    public IEnumerable<MatchData> Draw(IEnumerable<MatchData> matches, List<ModelAccuracy> accuracies) =>
        matches.Where(m =>
            _probabilityCalculator.CalculateDrawProbability(m, accuracies) >= PredictionThresholds.DrawStrongThreshold);

    public IEnumerable<MatchData> StraightWin(IEnumerable<MatchData> matches, List<ModelAccuracy> accuracies) =>
        matches.Where(m =>
            _probabilityCalculator.IsStrongHomeWin(m, accuracies) ||
            _probabilityCalculator.IsStrongAwayWin(m, accuracies));
}