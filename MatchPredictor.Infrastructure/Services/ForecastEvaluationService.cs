using System.Globalization;
using System.Text.RegularExpressions;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Infrastructure.Services;

public class ForecastEvaluationService : IForecastEvaluationService
{
    private const double BucketSize = 0.05;
    private static readonly TimeSpan PredictionLiveGrace = TimeSpan.FromMinutes(240);
    private static readonly Regex ScoreRegex = new(@"(\d+)\D+(\d+)", RegexOptions.Compiled);
    private static readonly Regex HandicapRegex = new(
        @"^(Home|Away)\s+([+-]?\d+(?:\.\d+)?)\s+Sets$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public AnalyticsStats CalculateStats(IEnumerable<Prediction> predictions, IEnumerable<ForecastObservation> forecasts)
    {
        var predictionList = PointInTimeBacktestingSelector.SelectPredictions(predictions)
            .Where(IsActiveAnalyticsPrediction)
            .ToList();
        var completedPredictions = predictionList
            .Where(IsPredictionCompletedForAnalytics)
            .ToList();

        var stats = new AnalyticsStats
        {
            TotalPredictions = predictionList.Count,
            CompletedPredictions = completedPredictions.Count,
            CorrectPredictions = completedPredictions.Count(IsPredictionCorrectForAnalytics),
            OverallAccuracy = completedPredictions.Count > 0
                ? (double)completedPredictions.Count(IsPredictionCorrectForAnalytics) / completedPredictions.Count
                : 0.0
        };

        foreach (var group in completedPredictions.GroupBy(prediction => prediction.PredictionCategory))
        {
            var total = group.Count();
            var correct = group.Count(IsPredictionCorrectForAnalytics);
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
                        var outcome = IsPredictionCorrectForAnalytics(prediction) ? 1.0 : 0.0;
                        var probability = (double)prediction.ConfidenceScore!.Value;
                        return Math.Pow(probability - outcome, 2);
                    })
                    : 0.0
            };
        }

        var settledForecasts = PointInTimeBacktestingSelector.SelectForecasts(forecasts)
            .Where(IsActiveAnalyticsForecast)
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

    private static bool IsActiveAnalyticsPrediction(Prediction prediction)
    {
        return prediction.PredictionCategory is "MatchWinner" or "OverUnderSets" or "SetHandicap";
    }

    private static bool IsActiveAnalyticsForecast(ForecastObservation forecast)
    {
        return forecast.Market is
            PredictionMarket.HomeWin or
            PredictionMarket.AwayWin or
            PredictionMarket.Over25Sets or
            PredictionMarket.Under25Sets or
            PredictionMarket.HomeSetHandicap or
            PredictionMarket.AwaySetHandicap;
    }

    private static bool IsPredictionCompletedForAnalytics(Prediction prediction)
    {
        if (IsPredictionActuallyLive(prediction))
        {
            return false;
        }

        return prediction.PredictionCategory switch
        {
            "MatchWinner" => TryParseSetsScore(prediction.ActualScore, out _, out _),
            "OverUnderSets" => TryParseSetsScore(prediction.ActualScore, out _, out _),
            "SetHandicap" => TryParseSetsScore(prediction.ActualScore, out _, out _) &&
                             TryParseHandicapPrediction(prediction.PredictedOutcome, out _, out _),
            _ => ResolvePredictionActualOutcome(prediction) is not null
        };
    }

    private static bool IsPredictionCorrectForAnalytics(Prediction prediction)
    {
        if (!TryParseSetsScore(prediction.ActualScore, out var homeSets, out var awaySets))
        {
            return OutcomesMatch(prediction.PredictedOutcome, ResolvePredictionActualOutcome(prediction));
        }

        return prediction.PredictionCategory switch
        {
            "MatchWinner" => OutcomesMatch(prediction.PredictedOutcome, ResolveMatchWinnerOutcome(homeSets, awaySets)),
            "OverUnderSets" => OutcomesMatch(prediction.PredictedOutcome, ResolveOverUnderSetsOutcome(homeSets, awaySets)),
            "SetHandicap" => DoesSetHandicapPredictionMatch(prediction.PredictedOutcome, homeSets, awaySets),
            _ => OutcomesMatch(prediction.PredictedOutcome, ResolvePredictionActualOutcome(prediction))
        };
    }

    private static string? ResolvePredictionActualOutcome(Prediction prediction)
    {
        if (!string.IsNullOrWhiteSpace(prediction.ActualOutcome) &&
            !string.Equals(prediction.ActualOutcome, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return prediction.ActualOutcome;
        }

        if (IsPredictionActuallyLive(prediction) ||
            !TryParseSetsScore(prediction.ActualScore, out var homeSets, out var awaySets))
        {
            return null;
        }

        return prediction.PredictionCategory switch
        {
            "MatchWinner" => ResolveMatchWinnerOutcome(homeSets, awaySets),
            "OverUnderSets" => ResolveOverUnderSetsOutcome(homeSets, awaySets),
            "SetHandicap" => ResolveSetHandicapOutcome(prediction.PredictedOutcome, homeSets, awaySets),
            _ => null
        };
    }

    private static bool IsPredictionActuallyLive(Prediction prediction)
    {
        if (!prediction.IsLive)
        {
            return false;
        }

        return !prediction.MatchDateTime.HasValue ||
               DateTime.UtcNow <= prediction.MatchDateTime.Value.Add(PredictionLiveGrace);
    }

    private static string ResolveMatchWinnerOutcome(int homeSets, int awaySets)
    {
        return homeSets > awaySets ? "Home Win" : "Away Win";
    }

    private static string ResolveOverUnderSetsOutcome(int homeSets, int awaySets)
    {
        return homeSets + awaySets > 2 ? "Over 2.5 Sets" : "Under 2.5 Sets";
    }

    private static string? ResolveSetHandicapOutcome(string predictedOutcome, int homeSets, int awaySets)
    {
        if (!TryParseHandicapPrediction(predictedOutcome, out var side, out var line))
        {
            return null;
        }

        return CoversHandicap(side, line, homeSets, awaySets)
            ? FormatSetHandicapOutcome(side, line)
            : FormatSetHandicapOutcome(side == "Home" ? "Away" : "Home", -line);
    }

    private static bool DoesSetHandicapPredictionMatch(string predictedOutcome, int homeSets, int awaySets)
    {
        return TryParseHandicapPrediction(predictedOutcome, out var side, out var line) &&
               CoversHandicap(side, line, homeSets, awaySets);
    }

    private static bool CoversHandicap(string side, double line, int homeSets, int awaySets)
    {
        return side.Equals("Home", StringComparison.OrdinalIgnoreCase)
            ? homeSets + line > awaySets
            : awaySets + line > homeSets;
    }

    private static bool TryParseHandicapPrediction(string predictedOutcome, out string side, out double line)
    {
        side = string.Empty;
        line = 0;

        if (string.IsNullOrWhiteSpace(predictedOutcome))
        {
            return false;
        }

        var match = HandicapRegex.Match(predictedOutcome.Trim());
        if (!match.Success)
        {
            return false;
        }

        side = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(match.Groups[1].Value.ToLowerInvariant());
        return double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out line);
    }

    private static string FormatSetHandicapOutcome(string side, double line)
    {
        return $"{side} {line:+0.0;-0.0} Sets";
    }

    private static bool TryParseSetsScore(string? score, out int homeSets, out int awaySets)
    {
        homeSets = 0;
        awaySets = 0;

        if (string.IsNullOrWhiteSpace(score))
        {
            return false;
        }

        var match = ScoreRegex.Match(score.Trim());
        if (!match.Success)
        {
            return false;
        }

        return int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out homeSets) &&
               int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out awaySets);
    }

    private static bool OutcomesMatch(string? expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual))
        {
            return false;
        }

        return string.Equals(expected.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase);
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
            CalibratedReliabilityCurve = BuildReliabilityCurve(calibratedInputs),
            CalibratorEraStats = BuildEraStats(
                settled,
                forecast => NormalizeCalibrator(forecast.CalibratorUsed),
                ["Bucket", "Beta", "Unknown"]),
            ThresholdEraStats = BuildEraStats(
                settled.Where(forecast => forecast.IsPublished),
                forecast => NormalizeThresholdSource(forecast.ThresholdSource),
                ["Configured", "Tuned", "Unknown"])
        };
    }

    private static List<EraPerformanceStat> BuildEraStats(
        IEnumerable<ForecastObservation> forecasts,
        Func<ForecastObservation, string> eraSelector,
        IReadOnlyList<string> preferredOrder)
    {
        var orderLookup = preferredOrder
            .Select((era, index) => (era, index))
            .ToDictionary(item => item.era, item => item.index, StringComparer.OrdinalIgnoreCase);

        return forecasts
            .Where(forecast => forecast.OutcomeOccurred.HasValue)
            .GroupBy(eraSelector)
            .Select(group => new EraPerformanceStat
            {
                Era = group.Key,
                Count = group.Count(),
                HitRate = group.Average(forecast => forecast.OutcomeOccurred == true ? 1.0 : 0.0),
                BrierScore = group.Average(forecast => SquaredError(forecast.CalibratedProbability, forecast.OutcomeOccurred!.Value))
            })
            .OrderBy(stat => orderLookup.TryGetValue(stat.Era, out var index) ? index : int.MaxValue)
            .ThenBy(stat => stat.Era)
            .ToList();
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

    private static string NormalizeCalibrator(string? calibratorUsed)
    {
        if (string.IsNullOrWhiteSpace(calibratorUsed) || calibratorUsed.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return "Unknown";
        }

        return calibratorUsed.Equals("Beta", StringComparison.OrdinalIgnoreCase) ? "Beta" : "Bucket";
    }

    private static string NormalizeThresholdSource(string? thresholdSource)
    {
        if (string.IsNullOrWhiteSpace(thresholdSource) || thresholdSource.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return "Unknown";
        }

        return thresholdSource.Equals("Tuned", StringComparison.OrdinalIgnoreCase) ? "Tuned" : "Configured";
    }
}
