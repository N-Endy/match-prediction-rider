namespace MatchPredictor.Domain.Models;

public class PredictionRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string RunKind { get; set; } = "prediction_generation";
    public string RunLabel { get; set; } = string.Empty;
    public string RunReason { get; set; } = string.Empty;
    public DateOnly TargetLocalDate { get; set; }
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
    public bool Succeeded { get; set; }
    public int ForecastCount { get; set; }
    public int PublishedPredictionCount { get; set; }
}
