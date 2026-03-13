namespace MatchPredictor.Domain.Models;

public class SourceMarketFixture
{
    public string EventId { get; set; } = string.Empty;
    public string League { get; set; } = string.Empty;
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public DateTime? MatchTimeUtc { get; set; }
    public double? HomeWinProbability { get; set; }
    public double? DrawProbability { get; set; }
    public double? AwayWinProbability { get; set; }
    public double? Over25Probability { get; set; }
    public double? BttsYesProbability { get; set; }
    public double? BttsNoProbability { get; set; }
}
