namespace MatchPredictor.Domain.Models;

public class VisitorSession
{
    public int Id { get; set; }
    public string VisitorId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string LandingPath { get; set; } = string.Empty;
    public string LastPath { get; set; } = string.Empty;
    public string Referrer { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}
