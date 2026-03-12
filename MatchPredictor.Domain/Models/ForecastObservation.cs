namespace MatchPredictor.Domain.Models;

public class ForecastObservation
{
    public int Id { get; set; }
    public string Date { get; set; } = string.Empty;
    public string Time { get; set; } = string.Empty;
    public DateTime? MatchDateTime { get; set; }
    public string League { get; set; } = string.Empty;
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public PredictionMarket Market { get; set; }
    public string PredictedOutcome { get; set; } = string.Empty;
    public double RawProbability { get; set; }
    public double CalibratedProbability { get; set; }
    public string CalibratorUsed { get; set; } = "Unknown";
    public double ThresholdUsed { get; set; }
    public string ThresholdSource { get; set; } = "Unknown";
    public bool? OutcomeOccurred { get; set; }
    public string? ActualOutcome { get; set; }
    public string? ActualScore { get; set; }
    public bool IsPublished { get; set; }
    public bool IsLive { get; set; }
    public bool IsSettled { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SettledAt { get; set; }
}
