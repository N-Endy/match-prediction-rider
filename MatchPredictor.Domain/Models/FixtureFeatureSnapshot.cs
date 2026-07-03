namespace MatchPredictor.Domain.Models;

public class FixtureFeatureSnapshot
{
    public int Id { get; set; }
    public string FixtureKey { get; set; } = string.Empty;
    public DateOnly MatchLocalDate { get; set; }
    public DateTime? MatchDateTimeUtc { get; set; }
    public string League { get; set; } = string.Empty;
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
    public string SourceName { get; set; } = "InternalHistory";
    public double? HomeRestDays { get; set; }
    public double? AwayRestDays { get; set; }
    public double? HomeFormPointsPerMatch { get; set; }
    public double? AwayFormPointsPerMatch { get; set; }
    public double? HomeFormGoalsForPerMatch { get; set; }
    public double? AwayFormGoalsForPerMatch { get; set; }
    public double? HomeFormGoalsAgainstPerMatch { get; set; }
    public double? AwayFormGoalsAgainstPerMatch { get; set; }
    public int HeadToHeadHomeWins { get; set; }
    public int HeadToHeadDraws { get; set; }
    public int HeadToHeadAwayWins { get; set; }
    public double? HomeExpectedGoalsFor { get; set; }
    public double? AwayExpectedGoalsFor { get; set; }
    public string RawPayloadJson { get; set; } = "{}";
}
