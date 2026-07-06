using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IThresholdTuningService
{
    double GetThreshold(PredictionMarket market, double fallbackThreshold, string? league = null);
    ThresholdDecision GetThresholdDecision(PredictionMarket market, double fallbackThreshold, string? league = null);
    Task RebuildProfilesAsync();
}
