namespace MatchPredictor.Domain.Models;

public class AiChatSessionState
{
    public List<ChatHistoryItem> History { get; set; } = [];
    public List<string> LastRecommendedActionKeys { get; set; } = [];
}
