using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IManualScoreLinkService
{
    Task<IReadOnlyDictionary<int, ScoreNearMissHint>> GetHintsAsync(
        IReadOnlyList<int> predictionIds,
        CancellationToken cancellationToken = default);

    Task<ManualScoreConfirmResult> ConfirmAsync(
        ManualScoreConfirmRequest request,
        CancellationToken cancellationToken = default);
}
