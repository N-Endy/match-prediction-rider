namespace MatchPredictor.Domain.Models;

public class UserActivityEvent
{
    public int Id { get; set; }
    public string VisitorId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string MetadataJson { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
