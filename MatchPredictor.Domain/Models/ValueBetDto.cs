namespace MatchPredictor.Domain.Models;

public class ValueBetDto
{
    public string League { get; set; } = null!;
    public string HomeTeam { get; set; } = null!;
    public string AwayTeam { get; set; } = null!;
    public string KickoffTime { get; set; } = null!;
    public string PredictionCategory { get; set; } = null!;
    public string PredictedOutcome { get; set; } = null!;
    public double MathematicalProbability { get; set; }
    public string AiJustification { get; set; } = null!;
}
