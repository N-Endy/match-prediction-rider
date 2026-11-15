namespace MatchPredictor.Domain.Models;

public class SourceMarketFixture
{
    public string EventId { get; set; } = string.Empty;
    public string League { get; set; } = string.Empty;
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public DateTime? MatchTimeUtc { get; set; }
    public double? HomeWinProbability { get; set; }
    public double? HomeWinOdds { get; set; }
    public double? DrawProbability { get; set; }
    public double? DrawOdds { get; set; }
    public double? AwayWinProbability { get; set; }
    public double? AwayWinOdds { get; set; }
    public double? Over25Probability { get; set; }
    public double? Over25Odds { get; set; }
    public double? Under25Probability { get; set; }
    public double? Under25Odds { get; set; }
    public double? BttsYesProbability { get; set; }
    public double? BttsYesOdds { get; set; }
    public double? BttsNoProbability { get; set; }
    public double? BttsNoOdds { get; set; }
    public double? Over15Probability { get; set; }
    public double? Over15Odds { get; set; }
    public double? Under15Probability { get; set; }
    public double? Under15Odds { get; set; }
    public double? DoubleChance1XProbability { get; set; }
    public double? DoubleChance1XOdds { get; set; }
    public double? DoubleChanceX2Probability { get; set; }
    public double? DoubleChanceX2Odds { get; set; }
    public double? DoubleChance12Probability { get; set; }
    public double? DoubleChance12Odds { get; set; }
}
