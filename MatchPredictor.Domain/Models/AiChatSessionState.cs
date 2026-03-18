namespace MatchPredictor.Domain.Models;

public class AiChatSessionState
{
    public List<ChatHistoryItem> History { get; set; } = [];
    public List<string> LastRecommendedActionKeys { get; set; } = [];
    public List<int> LastContextPredictionIds { get; set; } = [];
    public List<string> WorkingSlipActionKeys { get; set; } = [];
    public List<int> WorkingSlipPredictionIds { get; set; } = [];
    public List<int> LastDiscussedPredictionIds { get; set; } = [];
    public string LastIntent { get; set; } = string.Empty;
    public string LastKnowledgeTopic { get; set; } = string.Empty;
    public AiChatNormalizedRequest? LastNormalizedRequest { get; set; }
    public List<AiChatRequestedMarket> LastResolvedMarketMix { get; set; } = [];
    public List<string> LastShortfallWarnings { get; set; } = [];
    public bool AwaitingRolloverTargetOdds { get; set; }
    public string PendingRolloverPrompt { get; set; } = string.Empty;
    public AiChatNormalizedRequest? PendingNormalizedRequest { get; set; }
}
