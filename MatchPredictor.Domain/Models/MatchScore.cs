using System;

namespace MatchPredictor.Domain.Models;

public class MatchScore
{
    public int Id { get; set; }
    public string League { get; set; } = string.Empty;
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public string Score { get; set; } = string.Empty;
    public DateTime MatchTime { get; set; }
    public bool BTTSLabel { get; set; }
}
