using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IForecastEvaluationService
{
    AnalyticsStats CalculateStats(IEnumerable<Prediction> predictions, IEnumerable<ForecastObservation> forecasts);
}
