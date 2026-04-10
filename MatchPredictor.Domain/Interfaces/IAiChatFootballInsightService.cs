using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IAiChatFootballInsightService
{
    Task<IReadOnlyDictionary<string, FootballMatchInsightSnapshot>> GetInsightsAsync(
        IReadOnlyCollection<AiChatFootballInsightRequest> requests,
        CancellationToken ct = default);
}
