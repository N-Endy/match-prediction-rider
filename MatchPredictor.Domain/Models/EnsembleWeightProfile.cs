namespace MatchPredictor.Domain.Models;

/// <summary>
/// Learned per-market stacking weights for the ensemble blender. Rebuilt nightly from
/// settled forecast observations with a walk-forward holdout and only promoted when the
/// candidate beats the incumbent's holdout Brier score (see EnsembleWeightTuningService).
/// </summary>
public class EnsembleWeightProfile
{
    public int Id { get; set; }
    public PredictionMarket Market { get; set; }
    public double BookmakerWeight { get; set; }

    /// <summary>Weight of the sports-ai.dev feed signal (ProbabilityCalculator output).</summary>
    public double CalculatorWeight { get; set; }
    public double DixonColesWeight { get; set; }
    public int SampleCount { get; set; }
    public int HoldoutCount { get; set; }
    public double BaselineHoldoutBrier { get; set; }
    public double CandidateHoldoutBrier { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
