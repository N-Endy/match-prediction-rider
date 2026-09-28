using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IAiAppKnowledgeBase
{
    bool TryLookup(string query, out AppKnowledgeEntry? entry);
    IReadOnlyList<AppKnowledgeEntry> GetAllEntries();
}
