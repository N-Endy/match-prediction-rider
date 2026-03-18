using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IAiChatSchemaFallbackService
{
    Task<AiChatNormalizedRequest?> TryParseAsync(
        string userPrompt,
        AiChatNormalizedRequest deterministicRequest,
        CancellationToken ct = default);
}
