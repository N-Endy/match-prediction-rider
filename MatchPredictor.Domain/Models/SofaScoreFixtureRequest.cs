namespace MatchPredictor.Domain.Models;

public class SofaScoreFixtureRequest
{
    public string League { get; set; } = string.Empty;
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public DateOnly MatchLocalDate { get; set; }
    public DateTime? ScheduledMatchTimeUtc { get; set; }
    public string FixtureKey { get; set; } = string.Empty;
}
