using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IAiAdvisorService
{
    Task<AiChatResponse> GetAdviceAsync(string userPrompt, string sessionId, CancellationToken ct = default);
    Task<string> AnalyzeValueBetsAsync(string payload, CancellationToken ct = default);
}
