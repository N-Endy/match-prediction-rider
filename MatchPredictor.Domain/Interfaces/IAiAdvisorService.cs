using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IAiAdvisorService
{
    Task<AiChatResponse> GetAdviceAsync(string userPrompt, string sessionId, CancellationToken ct = default);

    async IAsyncEnumerable<AiChatStreamChunk> StreamAdviceAsync(
        string userPrompt,
        string sessionId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var response = await GetAdviceAsync(userPrompt, sessionId, ct);
        yield return new AiChatStreamChunk { EventType = "text", Content = response.Message };
        foreach (var card in response.KnowledgeCards)
        {
            yield return new AiChatStreamChunk { EventType = "card", Card = card };
        }
        foreach (var action in response.Actions)
        {
            yield return new AiChatStreamChunk { EventType = "action", Action = action };
        }
        yield return new AiChatStreamChunk { EventType = "done" };
    }

    Task<string> AnalyzeValueBetsAsync(string payload, CancellationToken ct = default);
    Task<IReadOnlyList<BetslipDrawPickSelection>> SelectBestDrawPicksAsync(
        IReadOnlyList<BetslipDrawPickRequest> candidates,
        int count = 5,
        CancellationToken ct = default);

    Task<BankerPickResult> SelectBankerPicksAsync(
        IReadOnlyList<BankerPickRequest> candidates,
        double minOdds,
        double maxOdds,
        CancellationToken ct = default);

    Task<BankerPickResult> SelectRolloverPickAsync(
        IReadOnlyList<BankerPickRequest> candidates,
        double minOdds,
        double maxOdds,
        CancellationToken ct = default);

    Task<LadderRankResult> RankLadderCandidatesAsync(
        IReadOnlyList<LadderRankRequest> candidates,
        CancellationToken ct = default);

    Task<BetslipScreenResult> ScreenBetslipCandidatesAsync(
        IReadOnlyList<BetslipScreenRequest> candidates,
        CancellationToken ct = default);

    Task<LadderComposeResult> ComposeLadderSlipsAsync(
        IReadOnlyList<LadderRankRequest> candidates,
        IReadOnlyList<LadderComposeBandRequest> bands,
        CancellationToken ct = default);
}
