namespace MatchPredictor.Domain.Models;

public sealed class StatisticalCoverageReport
{
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
    public int MatchScoreLookbackDays { get; init; }
    public int MatchScoreCount { get; init; }
    public int DistinctTeamsInScores { get; init; }
    public int ForecastLookbackDays { get; init; }
    public int SettledForecastCount { get; init; }
    public int ForecastsWithStatisticalSignal { get; init; }
    public double StatisticalSignalCoverage { get; init; }
    public int FixtureFeatureSnapshotCount { get; init; }
    public int SnapshotsWithNonNullForm { get; init; }
    public double FormFeatureCoverage { get; init; }
    public int TeamMatchStatsCount { get; init; }
    public int TeamMatchStatsWithXg { get; init; }
    public double XgCoverage { get; init; }
    public bool XgFeatureSchemaReady { get; init; }
    public string Notes { get; init; } = string.Empty;
}
