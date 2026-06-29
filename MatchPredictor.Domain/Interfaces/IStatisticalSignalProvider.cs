using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

/// <summary>
/// Produces statistical-model probabilities (Dixon-Coles goals model + Elo ratings)
/// for a batch of fixtures. These are blended with the market-based base model to
/// form the final raw probability that flows into correction/calibration.
/// Implementations build their models from historical results with a point-in-time
/// cutoff so no future result can leak into a prediction.
/// </summary>
public interface IStatisticalSignalProvider
{
    IStatisticalSignalSet BuildSignals(IReadOnlyCollection<MatchData> matches);
}

/// <summary>
/// A lookup of per-fixture statistical signals produced for one batch of matches.
/// </summary>
public interface IStatisticalSignalSet
{
    /// <summary>
    /// Returns the blended Dixon-Coles + Elo signal for a fixture, or null when there
    /// is not enough history for both teams to produce a trustworthy estimate.
    /// </summary>
    MatchProbabilities? GetSignal(MatchData match);
}
