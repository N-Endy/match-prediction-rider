namespace MatchPredictor.Domain.Models;

public class ScrapingLog
{
    public int Id { get; set; }
    public string EventName { get; set; } = "general";
    public string? SourceName { get; set; }
    public string? Stage { get; set; }
    public string? RunKind { get; set; }
    public string? RunLabel { get; set; }
    public Guid? PredictionRunId { get; set; }
    public DateTime Timestamp { get; set; }
    public string Status { get; set; } = "Failed";
    public string? Message { get; set; }
    public string? PayloadJson { get; set; }
}
