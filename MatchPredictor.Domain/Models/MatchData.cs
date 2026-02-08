namespace MatchPredictor.Domain.Models;

public class MatchData
{
    public int Id { get; set; }
    public string? Date { get; set; }
    public string? Time { get; set; }
    public string? League { get; set;}
    public string? HomeTeam { get; set; }
    public string? AwayTeam { get; set; }
    public double HomeWin { get; set; }
    public double Draw { get; set; }
    public double AwayWin { get; set; }
    public double OverTwoGoals { get; set; }
    public double OverThreeGoals { get; set; }
    public double UnderTwoGoals { get; set; }
    public double UnderThreeGoals { get; set; }
    public double OverFourGoals { get; set; }
    
    public string? Score { get; set; }
    public int BttsLabel { get; set; }
}
