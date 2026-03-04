namespace MatchPredictor.Domain.Models;

/// <summary>
/// Base class for match score data shared between different scraping sources.
/// </summary>
public abstract class MatchScoreBase
{
    public int Id { get; set; }
    public string League { get; set; } = string.Empty;
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public string Score { get; set; } = string.Empty;
    public DateTime MatchTime { get; set; }
    public bool BTTSLabel { get; set; }
    public bool IsLive { get; set; }
}
