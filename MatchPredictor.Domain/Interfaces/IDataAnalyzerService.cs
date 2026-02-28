using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IDataAnalyzerService
{
    IEnumerable<MatchData> BothTeamsScore(IEnumerable<MatchData> matches, List<ModelAccuracy> accuracies);
    IEnumerable<MatchData> OverTwoGoals(IEnumerable<MatchData> matches, List<ModelAccuracy> accuracies);
    IEnumerable<MatchData> Draw(IEnumerable<MatchData> matches, List<ModelAccuracy> accuracies);
    IEnumerable<MatchData> StraightWin(IEnumerable<MatchData> matches, List<ModelAccuracy> accuracies);
}