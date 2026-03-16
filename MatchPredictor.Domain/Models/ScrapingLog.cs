namespace MatchPredictor.Domain.Models;

public class ScrapingLog
{
    public int Id { get; set; }
    public string EventName { get; set; } = "general";
    public DateTime Timestamp { get; set; }
    public string Status { get; set; } = "Failed";
    public string? Message { get; set; }
}
