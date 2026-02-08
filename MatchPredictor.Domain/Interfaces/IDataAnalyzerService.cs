using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IDataAnalyzerService
{
    IEnumerable<MatchData> BothTeamsScore(IEnumerable<MatchData> matches);
    IEnumerable<MatchData> OverTwoGoals(IEnumerable<MatchData> matches);
    IEnumerable<MatchData> Draw(IEnumerable<MatchData> matches);
    IEnumerable<MatchData> StraightWin(IEnumerable<MatchData> matches);
}