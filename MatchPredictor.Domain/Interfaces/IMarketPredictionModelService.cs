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

    double? TryPredictChallenger(
        MatchData match,
        PredictionMarket market,
        double calculatorProbability,
        double? statisticalProbability,
        double? bookmakerProbability);

    Task TrainChallengerProfilesAsync(CancellationToken ct = default);

    Task LogShadowPredictionAsync(
        int predictionId,
        string fixtureKey,
        PredictionMarket market,
        string predictedOutcome,
        double championProbability,
        double challengerProbability,
        CancellationToken ct = default);

    Task EvaluateAndSettleShadowPredictionsAsync(CancellationToken ct = default);
}
