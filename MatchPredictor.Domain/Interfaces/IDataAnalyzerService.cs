using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IDataAnalyzerService
{
    IReadOnlyList<PredictionCandidate> BuildForecastCandidates(IEnumerable<MatchData> matches);
    IReadOnlyList<PredictionCandidate> SelectPublishedPredictions(IEnumerable<PredictionCandidate> forecastCandidates);
    IReadOnlyList<PredictionCandidate> MatchWinner(IEnumerable<MatchData> matches);
    IReadOnlyList<PredictionCandidate> OverUnderSets(IEnumerable<MatchData> matches);
    IReadOnlyList<PredictionCandidate> SetHandicap(IEnumerable<MatchData> matches);
}
