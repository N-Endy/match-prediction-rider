
namespace MatchPredictor.Domain.Models;

public class Prediction
{
    public int Id { get; set; }
    public string Date { get; set; } = null!;
    public string Time { get; set; } = null!;
    public string League { get; set; } = null!;
    public string HomeTeam { get; set; } = null!;
    public string AwayTeam { get; set; } = null!;
    public string PredictionCategory { get; set; } = null!;
    public string PredictedOutcome { get; set; } = null!;
    public decimal? ConfidenceScore { get; set; }
    public string? ActualOutcome { get; set; }
    public string? ActualScore { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
