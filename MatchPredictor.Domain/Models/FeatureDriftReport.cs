namespace MatchPredictor.Domain.Models;

public sealed record FeatureDriftReport(
    DateTime GeneratedAtUtc,
    int ReferenceWindowDays,
    int CurrentWindowDays,
    int ReferenceSnapshotCount,
    int CurrentSnapshotCount,
    IReadOnlyList<FeatureDriftResult> FeatureResults,
    bool HasSignificantDrift,
    bool HasModerateDrift,
    string RecommendedAction);
