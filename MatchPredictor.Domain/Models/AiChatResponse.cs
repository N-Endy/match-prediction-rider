namespace MatchPredictor.Domain.Models;

public class AiChatResponse
{
    public string Message { get; set; } = string.Empty;
    public string ContextMode { get; set; } = string.Empty;
    public List<AiChatAction> Actions { get; set; } = [];
    public bool ShowBookAll { get; set; }
    public List<string> Warnings { get; set; } = [];
    public List<string> SuggestedPrompts { get; set; } = [];
    public AiChatWorkingSlipSummary? WorkingSlipSummary { get; set; }
    public List<AiChatKnowledgeCard> KnowledgeCards { get; set; } = [];
}
