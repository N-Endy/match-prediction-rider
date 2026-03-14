namespace MatchPredictor.Domain.Models;

public class AiChatSessionState
{
    public List<ChatHistoryItem> History { get; set; } = [];
    public List<string> LastRecommendedActionKeys { get; set; } = [];
    public bool AwaitingRolloverTargetOdds { get; set; }
    public string PendingRolloverPrompt { get; set; } = string.Empty;
}
