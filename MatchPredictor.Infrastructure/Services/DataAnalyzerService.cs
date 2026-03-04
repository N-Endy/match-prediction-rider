using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using Microsoft.Extensions.Options;

namespace MatchPredictor.Infrastructure.Services;

public class DataAnalyzerService : IDataAnalyzerService
{
    private readonly IProbabilityCalculator _probabilityCalculator;
    private readonly PredictionSettings _settings;

    public DataAnalyzerService(IProbabilityCalculator probabilityCalculator, IOptions<PredictionSettings> options)
    {
        _probabilityCalculator = probabilityCalculator;
        _settings = options.Value;
    }

    public IEnumerable<MatchData> BothTeamsScore(IEnumerable<MatchData> matches, List<ModelAccuracy> accuracies) =>
        matches.Where(m =>
            _probabilityCalculator.CalculateBttsProbability(m, accuracies) >= _settings.BttsScoreThreshold);

    public IEnumerable<MatchData> OverTwoGoals(IEnumerable<MatchData> matches, List<ModelAccuracy> accuracies) =>
        matches.Where(m =>
            _probabilityCalculator.CalculateOverTwoGoalsProbability(m, accuracies) >= _settings.OverTwoGoalsStrongThreshold);

    public IEnumerable<MatchData> Draw(IEnumerable<MatchData> matches, List<ModelAccuracy> accuracies) =>
        matches.Where(m =>
            _probabilityCalculator.CalculateDrawProbability(m, accuracies) >= _settings.DrawStrongThreshold);

    public IEnumerable<MatchData> StraightWin(IEnumerable<MatchData> matches, List<ModelAccuracy> accuracies) =>
        matches.Where(m =>
            _probabilityCalculator.IsStrongHomeWin(m, accuracies) ||
            _probabilityCalculator.IsStrongAwayWin(m, accuracies));
}