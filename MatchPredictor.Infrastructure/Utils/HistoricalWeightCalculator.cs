using MatchPredictor.Domain.Models;

namespace MatchPredictor.Infrastructure.Utils;

/// <summary>
/// Calculates historical self-learning weights from model accuracy data.
/// Shared between ProbabilityCalculator and RegressionPredictorService.
/// </summary>
public static class HistoricalWeightCalculator
{
    /// <summary>
    /// Calculates an adjustment weight based on historical accuracy for a given category.
    /// Uses a fallback chain of metrics — stops at the first one with enough statistical data.
    /// Returns a value between 0.7 and 1.3:
    /// - Above 1.0 = historically accurate → boost confidence
    /// - Below 1.0 = historically inaccurate → reduce confidence
    /// </summary>
    public static double GetHistoricalWeight(List<ModelAccuracy> accuracies, string category, params (string MetricName, double MetricValue)[] fallbacks)
    {
        if (accuracies == null || accuracies.Count == 0 || fallbacks == null || fallbacks.Length == 0) return 1.0;

        foreach (var (metricName, metricValue) in fallbacks)
        {
            if (metricValue <= 0) continue;

            var profile = accuracies.FirstOrDefault(a => 
                a.Category == category && 
                a.MetricName == metricName && 
                metricValue >= a.MetricRangeStart && 
                metricValue < a.MetricRangeEnd);

            if (profile != null && profile.TotalPredictions >= 5)
            {
                var weight = 1.0 + (profile.AccuracyPercentage - 0.50);
                return Math.Clamp(weight, 0.7, 1.3);
            }
        }

        return 1.0;
    }
}
