namespace MatchPredictor.Domain.Models;

public class AiScoreMatchScore
{
    public int Id { get; set; }
    public string League { get; set; } = string.Empty;
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public string Score { get; set; } = string.Empty;
    public DateTime MatchTime { get; set; }
    public DateOnly MatchLocalDate { get; set; }
    public string HomeTeamKey { get; set; } = string.Empty;
    public string AwayTeamKey { get; set; } = string.Empty;
    public string LeagueKey { get; set; } = string.Empty;
    public string? SourceEventId { get; set; }
    public string? HomeTeamId { get; set; }
    public string? AwayTeamId { get; set; }
    public bool BTTSLabel { get; set; }
    public bool IsLive { get; set; }
}
