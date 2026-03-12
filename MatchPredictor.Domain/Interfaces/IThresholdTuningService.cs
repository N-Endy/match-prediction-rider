using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IThresholdTuningService
{
    double GetThreshold(PredictionMarket market, double fallbackThreshold);
    ThresholdDecision GetThresholdDecision(PredictionMarket market, double fallbackThreshold);
    Task RebuildProfilesAsync();
}
