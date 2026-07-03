using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IDataAnalyzerService
{
    IReadOnlyList<PredictionCandidate> BuildForecastCandidates(IEnumerable<MatchData> matches);

    /// <summary>
    /// Builds forecast candidates with an optional de-vigged bookmaker signal per fixture.
    /// The default implementation ignores the bookmaker signal so existing implementations
    /// and test doubles keep working.
    /// </summary>
    IReadOnlyList<PredictionCandidate> BuildForecastCandidates(
        IEnumerable<MatchData> matches,
        BookmakerSignalSet? bookmakerSignals)
        => BuildForecastCandidates(matches);

    IReadOnlyList<PredictionCandidate> SelectPublishedPredictions(IEnumerable<PredictionCandidate> forecastCandidates);
    IReadOnlyList<PredictionCandidate> BothTeamsScore(IEnumerable<MatchData> matches);
    IReadOnlyList<PredictionCandidate> OverTwoGoals(IEnumerable<MatchData> matches);
    IReadOnlyList<PredictionCandidate> UnderTwoGoals(IEnumerable<MatchData> matches);
    IReadOnlyList<PredictionCandidate> StraightWin(IEnumerable<MatchData> matches);
}
