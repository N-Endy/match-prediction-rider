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
    
    // Granular Over/Under lines for better goal expectation modeling
    public double OverOneGoal { get; set; }
    public double OverOnePointFive { get; set; }
    public double UnderOnePointFive { get; set; }
    
    // Asian Handicap data — key for implied goal expectation
    // ah_0: level handicap (pure win probability without draw)
    public double AhZeroHome { get; set; }
    public double AhZeroAway { get; set; }
    // ah_-0.5: most important AH line — home must win by 1+ goal
    public double AhMinusHalfHome { get; set; }
    public double AhMinusHalfAway { get; set; }
    // ah_-1: home must win by 2+ goals
    public double AhMinusOneHome { get; set; }
    public double AhMinusOneAway { get; set; }
    // ah_+0.5: away doesn't lose (draw or win)
    public double AhPlusHalfHome { get; set; }
    public double AhPlusHalfAway { get; set; }
    
    public string? Score { get; set; }
    public int BttsLabel { get; set; }
}
