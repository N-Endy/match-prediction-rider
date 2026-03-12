
namespace MatchPredictor.Domain.Models;

public class Prediction
{
    public int Id { get; set; }
    public string Date { get; set; } = null!;
    public string Time { get; set; } = null!;
    public DateTime? MatchDateTime { get; set; }
    public string League { get; set; } = null!;
    public string HomeTeam { get; set; } = null!;
    public string AwayTeam { get; set; } = null!;
    public string PredictionCategory { get; set; } = null!;
    public string PredictedOutcome { get; set; } = null!;
    public decimal? RawConfidenceScore { get; set; }
    public decimal? ConfidenceScore { get; set; }
    public string CalibratorUsed { get; set; } = "Unknown";
    public double ThresholdUsed { get; set; }
    public string ThresholdSource { get; set; } = "Unknown";
    public bool WasPublished { get; set; } = true;
    public string? ActualOutcome { get; set; }
    public string? ActualScore { get; set; }
    public bool IsLive { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
