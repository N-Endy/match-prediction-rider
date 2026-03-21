namespace MatchPredictor.Domain.Models;

public class SofaScoreMatchScore
{
    public int Id { get; set; }
    public string League { get; set; } = string.Empty;
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public string Score { get; set; } = string.Empty;
    public string? DisplayedScore { get; set; }
    public string? RegularTimeScore { get; set; }
    public string? HalfTimeScore { get; set; }
    public string? ExtraTimeScore { get; set; }
    public string? StatusText { get; set; }
    public string EventUrl { get; set; } = string.Empty;
    public DateTime MatchTime { get; set; }
    public bool BTTSLabel { get; set; }
    public bool IsLive { get; set; }
}
