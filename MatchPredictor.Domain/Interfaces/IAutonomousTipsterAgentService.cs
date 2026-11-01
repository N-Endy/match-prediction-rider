using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IAutonomousTipsterAgentService
{
    Task<OperationsDossierDto?> ReasonAndComposeDossierAsync(
        DateOnly slateDate,
        IReadOnlyList<Prediction> upcomingPredictions,
        IReadOnlyDictionary<int, decimal> resolvedOddsLookup,
        int totalFixturesOnDate,
        CancellationToken ct = default);
}
