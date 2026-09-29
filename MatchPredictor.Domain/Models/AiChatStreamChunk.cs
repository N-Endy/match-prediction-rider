namespace MatchPredictor.Domain.Models;

public class AiChatStreamChunk
{
    public string EventType { get; set; } = "text";
    public string? Content { get; set; }
    public AiChatKnowledgeCard? Card { get; set; }
    public AiChatAction? Action { get; set; }
    public List<string>? Warnings { get; set; }
    public List<string>? SuggestedPrompts { get; set; }
    public AiChatWorkingSlipSummary? WorkingSlipSummary { get; set; }
    public string? ContextMode { get; set; }
    public bool ShowBookAll { get; set; }
    public bool AutoBook { get; set; }
}
