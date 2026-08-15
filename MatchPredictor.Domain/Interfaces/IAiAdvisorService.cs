using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IAiAdvisorService
{
    Task<AiChatResponse> GetAdviceAsync(string userPrompt, string sessionId, CancellationToken ct = default);
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

    Task<LadderRankResult> RankLadderCandidatesAsync(
        IReadOnlyList<LadderRankRequest> candidates,
        CancellationToken ct = default);
}
