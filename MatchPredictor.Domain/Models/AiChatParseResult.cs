namespace MatchPredictor.Domain.Models;

public class AiChatParseResult
{
    public AiChatNormalizedRequest Request { get; set; } = new();
    public bool UsedSemanticFallback { get; set; }
    public List<string> ValidationWarnings { get; set; } = [];
}
