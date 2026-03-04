namespace MatchPredictor.Domain.Models;

public class ChatHistoryItem
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}
