using MatchPredictor.Domain.Models;

namespace MatchPredictor.Infrastructure.Services;

public interface ILiveInPlayResearchEngine
{
    LiveInPlayAnalysis Analyze(AiChatContextBuilder.AiChatContextCandidate candidate, DateTime nowUtc);
    AiChatAction CreateInPlayAction(AiChatContextBuilder.AiChatContextCandidate candidate, LiveInPlayAnalysis analysis);
    string BuildInPlaySummaryMessage(IReadOnlyList<LiveInPlayAnalysis> analyses, string userPrompt);
    string BuildInPlaySystemPrompt(IReadOnlyList<LiveInPlayAnalysis> analyses, string userPrompt);
}
