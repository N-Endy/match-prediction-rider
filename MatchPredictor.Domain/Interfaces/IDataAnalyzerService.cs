using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IDataAnalyzerService
{
    IReadOnlyList<PredictionCandidate> BuildForecastCandidates(IEnumerable<MatchData> matches);
    IReadOnlyList<PredictionCandidate> SelectPublishedPredictions(IEnumerable<PredictionCandidate> forecastCandidates);
    IReadOnlyList<PredictionCandidate> BothTeamsScore(IEnumerable<MatchData> matches);
    IReadOnlyList<PredictionCandidate> OverTwoGoals(IEnumerable<MatchData> matches);
    IReadOnlyList<PredictionCandidate> Draw(IEnumerable<MatchData> matches);
    IReadOnlyList<PredictionCandidate> StraightWin(IEnumerable<MatchData> matches);
}
