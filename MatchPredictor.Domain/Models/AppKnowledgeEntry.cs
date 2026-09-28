namespace MatchPredictor.Domain.Models;

public class AppKnowledgeEntry
{
    public string TopicId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string[] Keywords { get; set; } = [];
    public List<AiChatKnowledgeCard> Cards { get; set; } = [];
}
