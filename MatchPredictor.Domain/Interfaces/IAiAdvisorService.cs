using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IAiAdvisorService
{
    Task<string> GetAdviceAsync(string userPrompt, List<ChatHistoryItem>? history = null, CancellationToken ct = default);
}
