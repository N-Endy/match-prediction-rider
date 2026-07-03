using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

/// <summary>
/// Supplies the ensemble blender with per-market stacking weights. Learned profiles
/// (rebuilt nightly, promotion-gated) override the configured defaults per market.
/// </summary>
public interface IEnsembleWeightProvider
{
    EnsembleWeights GetWeights(PredictionMarket market, EnsembleWeights fallback);
}

/// <summary>
/// Nightly learning-loop step that refits per-market ensemble stacking weights from
/// settled forecast observations using a walk-forward holdout, promoting a candidate
/// only when it beats the incumbent's holdout Brier score.
/// </summary>
public interface IEnsembleWeightTuningService : IEnsembleWeightProvider
{
    Task RebuildProfilesAsync();
}
