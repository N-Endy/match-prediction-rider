namespace MatchPredictor.Domain.Models;

public class SourceQualityProfile
{
    public int Id { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public string LeagueKey { get; set; } = string.Empty;
    public string LeagueLabel { get; set; } = string.Empty;
    public string TimeBucketKey { get; set; } = string.Empty;
    public string TimeBucketLabel { get; set; } = string.Empty;
    public int SampleCount { get; set; }
    public int FinishedCoverageCount { get; set; }
    public int ExactScoreMatchCount { get; set; }
    public int LiveOnlyCount { get; set; }
    public double AverageKickoffOffsetMinutes { get; set; }
    public double ReliabilityScore { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    public double FinishedCoverageRate => SampleCount > 0
        ? FinishedCoverageCount / (double)SampleCount
        : 0.0;

    public double ExactScoreMatchRate => FinishedCoverageCount > 0
        ? ExactScoreMatchCount / (double)FinishedCoverageCount
        : 0.0;

    public double LiveOnlyRate => SampleCount > 0
        ? LiveOnlyCount / (double)SampleCount
        : 0.0;
}
