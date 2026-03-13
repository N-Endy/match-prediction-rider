namespace MatchPredictor.Domain.Models;

public class AiChatResponse
{
    public string Message { get; set; } = string.Empty;
    public List<AiChatAction> Actions { get; set; } = [];
    public bool ShowBookAll { get; set; }
    public List<string> Warnings { get; set; } = [];
}
