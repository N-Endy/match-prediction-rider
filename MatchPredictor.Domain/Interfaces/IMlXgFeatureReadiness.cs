namespace MatchPredictor.Domain.Interfaces;

/// <summary>
/// Guards LightGBM FeatureColumns schema bumps that add xG until TeamMatchStats
/// coverage clears the promotion-safe threshold (~70% of scored fixtures).
/// </summary>
public interface IMlXgFeatureReadiness
{
    const double MinimumXgCoverage = 0.70;

    Task<MlXgFeatureReadinessResult> EvaluateAsync(CancellationToken cancellationToken = default);
}

public sealed class MlXgFeatureReadinessResult
{
    public double XgCoverage { get; init; }
    public int SampleSize { get; init; }
    public int WithXg { get; init; }
    public bool MayBumpFeatureSchema { get; init; }
    public string Reason { get; init; } = string.Empty;
}
