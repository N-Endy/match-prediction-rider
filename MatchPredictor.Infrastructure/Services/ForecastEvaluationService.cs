using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Infrastructure.Services;

public class ForecastEvaluationService : IForecastEvaluationService
{
    private const double BucketSize = 0.05;

    public AnalyticsStats CalculateStats(IEnumerable<Prediction> predictions, IEnumerable<ForecastObservation> forecasts)
    {
        var predictionList = predictions.ToList();
        var completedPredictions = predictionList
            .Where(prediction => !prediction.IsLive && !string.IsNullOrEmpty(prediction.ActualOutcome))
            .ToList();

        var stats = new AnalyticsStats
        {
            TotalPredictions = predictionList.Count,
            CompletedPredictions = completedPredictions.Count,
            CorrectPredictions = completedPredictions.Count(prediction => prediction.PredictedOutcome == prediction.ActualOutcome),
            OverallAccuracy = completedPredictions.Count > 0
                ? (double)completedPredictions.Count(prediction => prediction.PredictedOutcome == prediction.ActualOutcome) / completedPredictions.Count
                : 0.0
        };

        foreach (var group in completedPredictions.GroupBy(prediction => prediction.PredictionCategory))
        {
            var total = group.Count();
            var correct = group.Count(prediction => prediction.PredictedOutcome == prediction.ActualOutcome);
            var scoredPredictions = group.Where(prediction => prediction.ConfidenceScore.HasValue).ToList();

            stats.CategoryStats[group.Key] = new CategoryStat
            {
                Category = group.Key,
                Total = total,
                Correct = correct,
                Accuracy = total > 0 ? (double)correct / total : 0.0,
                BrierScore = scoredPredictions.Count > 0
                    ? scoredPredictions.Average(prediction =>
                    {
                        var outcome = prediction.PredictedOutcome == prediction.ActualOutcome ? 1.0 : 0.0;
                        var probability = (double)prediction.ConfidenceScore!.Value;
                        return Math.Pow(probability - outcome, 2);
                    })
                    : 0.0
            };
        }

        var settledForecasts = forecasts
            .Where(forecast => forecast.IsSettled && forecast.OutcomeOccurred.HasValue)
            .ToList();

        stats.SettledForecasts = settledForecasts.Count;

        if (settledForecasts.Count > 0)
        {
            stats.RawBrierScore = settledForecasts
                .Average(forecast => SquaredError(forecast.RawProbability, forecast.OutcomeOccurred!.Value));
            stats.BrierScore = settledForecasts
                .Average(forecast => SquaredError(forecast.CalibratedProbability, forecast.OutcomeOccurred!.Value));

            stats.ForecastMarketStats = settledForecasts
                .GroupBy(forecast => forecast.Market)
                .OrderBy(group => group.Key)
                .Select(BuildMarketStat)
                .ToList();
        }

        return stats;
    }

    private static ForecastMarketStat BuildMarketStat(IGrouping<PredictionMarket, ForecastObservation> group)
    {
        var settled = group.ToList();
        var rawInputs = settled
            .Select(forecast => (Probability: forecast.RawProbability, Outcome: forecast.OutcomeOccurred!.Value))
            .ToList();
        var calibratedInputs = settled
            .Select(forecast => (Probability: forecast.CalibratedProbability, Outcome: forecast.OutcomeOccurred!.Value))
            .ToList();

        return new ForecastMarketStat
        {
            Market = group.Key,
            MarketName = group.Key.ToDisplayName(),
            SettledCount = settled.Count,
            RawBrierScore = rawInputs.Count > 0 ? rawInputs.Average(input => SquaredError(input.Probability, input.Outcome)) : 0.0,
            CalibratedBrierScore = calibratedInputs.Count > 0 ? calibratedInputs.Average(input => SquaredError(input.Probability, input.Outcome)) : 0.0,
            RawDecomposition = BuildDecomposition(rawInputs),
            CalibratedDecomposition = BuildDecomposition(calibratedInputs),
            RawReliabilityCurve = BuildReliabilityCurve(rawInputs),
            CalibratedReliabilityCurve = BuildReliabilityCurve(calibratedInputs)
        };
    }

    private static BrierDecomposition BuildDecomposition(IReadOnlyCollection<(double Probability, bool Outcome)> inputs)
    {
        if (inputs.Count == 0)
        {
            return new BrierDecomposition();
        }

        var outcomes = inputs.Select(input => input.Outcome ? 1.0 : 0.0).ToList();
        var overallObservedRate = outcomes.Average();
        var reliability = 0.0;
        var resolution = 0.0;

        foreach (var group in inputs.GroupBy(input => GetBucketStart(input.Probability)))
        {
            var count = group.Count();
            var averageProbability = group.Average(item => item.Probability);
            var observedFrequency = group.Average(item => item.Outcome ? 1.0 : 0.0);
            var weight = count / (double)inputs.Count;

            reliability += weight * Math.Pow(averageProbability - observedFrequency, 2);
            resolution += weight * Math.Pow(observedFrequency - overallObservedRate, 2);
        }

        return new BrierDecomposition
        {
            Score = inputs.Average(input => SquaredError(input.Probability, input.Outcome)),
            Reliability = reliability,
            Resolution = resolution,
            Uncertainty = overallObservedRate * (1.0 - overallObservedRate)
        };
    }

    private static List<ReliabilityCurvePoint> BuildReliabilityCurve(IReadOnlyCollection<(double Probability, bool Outcome)> inputs)
    {
        if (inputs.Count == 0)
        {
            return [];
        }

        return inputs
            .GroupBy(input => GetBucketStart(input.Probability))
            .OrderBy(group => group.Key)
            .Select(group => new ReliabilityCurvePoint
            {
                BucketStart = group.Key,
                BucketEnd = Math.Min(group.Key + BucketSize, 1.0),
                AveragePredictedProbability = group.Average(item => item.Probability),
                ObservedFrequency = group.Average(item => item.Outcome ? 1.0 : 0.0),
                Count = group.Count()
            })
            .ToList();
    }

    private static double SquaredError(double probability, bool outcome)
    {
        return Math.Pow(Math.Clamp(probability, 0.0, 1.0) - (outcome ? 1.0 : 0.0), 2);
    }

    private static double GetBucketStart(double probability)
    {
        var clamped = Math.Clamp(probability, 0.0, 0.999999);
        return Math.Floor(clamped / BucketSize) * BucketSize;
    }
}
