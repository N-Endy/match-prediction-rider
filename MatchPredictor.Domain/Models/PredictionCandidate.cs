namespace MatchPredictor.Domain.Models;

public class PredictionCandidate
{
    public PredictionMarket Market { get; set; }
    public string Date { get; set; } = string.Empty;
    public string Time { get; set; } = string.Empty;
    public DateTime? MatchDateTime { get; set; }
    public string League { get; set; } = string.Empty;
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public string PredictionCategory { get; set; } = string.Empty;
    public string PredictedOutcome { get; set; } = string.Empty;
    public double RawProbability { get; set; }
    public double CalibratedProbability { get; set; }
    public string CalibratorUsed { get; set; } = "Bucket";
    public double ThresholdUsed { get; set; }
    public string ThresholdSource { get; set; } = "Configured";
    public bool WasPublished { get; set; }
}
