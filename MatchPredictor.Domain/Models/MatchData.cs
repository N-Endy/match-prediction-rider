namespace MatchPredictor.Domain.Models;

public class MatchData
{
    public int Id { get; set; }
    public string? Date { get; set; }
    public string? Time { get; set; }
    public DateOnly? MatchLocalDate { get; set; }
    public TimeOnly? MatchLocalTime { get; set; }
    public DateTime? MatchDateTime { get; set; }
    public string FixtureKey { get; set; } = string.Empty;
    public string? League { get; set; }
    public string? Tournament { get; set; }
    public string? Surface { get; set; }
    public string? HomeTeam { get; set; }
    public string? AwayTeam { get; set; }
    public double HomeWin { get; set; }
    public double AwayWin { get; set; }
    public double OverTwoPointFiveSets { get; set; }
    public double UnderTwoPointFiveSets { get; set; }
    public double SetHandicapHome { get; set; }
    public double SetHandicapAway { get; set; }
    public double SetHandicapLine { get; set; } = 1.5;
    public string? SetHandicapLabel { get; set; }
    public string? SourceMatchId { get; set; }
    public string? Score { get; set; }
    public string? NormalizedScoreline { get; set; }
    public int? HomeSetsWon { get; set; }
    public int? AwaySetsWon { get; set; }
}
