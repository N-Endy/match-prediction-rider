namespace MatchPredictor.Domain.Interfaces;

/// <summary>
/// Orchestrates the nightly retraining of the prediction learning loop:
/// probability correction (meta-model), calibration, and publish thresholds.
/// </summary>
public interface ILearningLoopService
{
    /// <summary>
    /// Rebuilds all learning profiles. Each rebuild step is isolated so a failure
    /// in one does not prevent the others from running.
    /// </summary>
    Task RebuildAllProfilesAsync();
}
