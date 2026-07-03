using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IMarketPredictionModelService
{
    Task RebuildProfilesAsync(CancellationToken cancellationToken = default);

    double? TryPredict(
        MatchData match,
        PredictionMarket market,
        double calculatorProbability,
        double? statisticalProbability,
        double? bookmakerProbability);
}
