using System;

namespace MatchPredictor.Domain.Models;

public class MatchScore
{
    public int Id { get; set; }
    public string League { get; set; } = string.Empty;
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public string Score { get; set; } = string.Empty;
    public string? NormalizedScoreline { get; set; }
    public int? HomeSetsWon { get; set; }
    public int? AwaySetsWon { get; set; }
    public DateTime MatchTime { get; set; }
    public bool IsLive { get; set; }
}
