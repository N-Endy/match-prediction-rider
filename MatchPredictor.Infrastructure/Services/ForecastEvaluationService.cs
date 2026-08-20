using System.Text.Json;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Infrastructure.Services;

public class ForecastEvaluationService : IForecastEvaluationService
{
    private const double BucketSize = 0.05;
    private const double ConfidenceBandSize = 0.10;
    // Quarter-Kelly is the staking convention used for the staking-adjusted return KPI.
    private const double KellyFraction = BetPricingMath.DefaultKellyFraction;
    // Cap on the number of per-forecast explainability rows surfaced per window.
    private const int MaxFeatureDiagnostics = 40;

    public AnalyticsStats CalculateStats(
        IEnumerable<Prediction> predictions,
        IEnumerable<ForecastObservation> forecasts,
        IEnumerable<PredictionOddsSnapshot>? oddsSnapshots = null)
    {
        var publishedPredictions = predictions
            .Where(prediction => prediction.WasPublished)
            .ToList();
        var predictionList = PointInTimeBacktestingSelector.SelectPredictions(publishedPredictions)
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
        stats.Precision = CalculatePrecision(completedPredictions);
        stats.Recall = CalculateRecall(completedPredictions);
        stats.F1Score = CalculateF1(stats.Precision, stats.Recall);

        foreach (var group in completedPredictions.GroupBy(prediction => prediction.PredictionCategory))
        {
            var total = group.Count();
            var correct = group.Count(IsPredictionCorrectForAnalytics);
            var scoredPredictions = group.Where(prediction => prediction.ConfidenceScore.HasValue).ToList();
            var outcomes = scoredPredictions
                .Select(prediction => (
                    Probability: (double)Math.Clamp(prediction.ConfidenceScore!.Value, 0m, 1m),
                    Outcome: IsPredictionCorrectForAnalytics(prediction)))
                .ToList();

            stats.CategoryStats[group.Key] = new CategoryStat
            {
                Category = group.Key,
                DisplayName = MapCategoryToDisplayName(group.Key),
                Total = total,
                Correct = correct,
                Accuracy = total > 0 ? (double)correct / total : 0.0,
                BrierScore = scoredPredictions.Count > 0
                    ? scoredPredictions.Average(prediction =>
                        SquaredError(
                            (double)prediction.ConfidenceScore!.Value,
                            IsPredictionCorrectForAnalytics(prediction)))
                    : 0.0,
                LogLoss = outcomes.Count > 0 ? outcomes.Average(item => BinaryLogLoss(item.Probability, item.Outcome)) : 0.0,
                Precision = CalculatePrecision(group),
                Recall = CalculateRecall(group),
                F1Score = CalculateF1(CalculatePrecision(group), CalculateRecall(group))
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
            stats.LogLoss = settledForecasts
                .Average(forecast => BinaryLogLoss(forecast.CalibratedProbability, forecast.OutcomeOccurred!.Value));
            stats.RawExpectedCalibrationError = CalculateExpectedCalibrationError(
                settledForecasts.Select(forecast => (forecast.RawProbability, forecast.OutcomeOccurred!.Value)));
            stats.ExpectedCalibrationError = CalculateExpectedCalibrationError(
                settledForecasts.Select(forecast => (forecast.CalibratedProbability, forecast.OutcomeOccurred!.Value)));
            var overallObservedRate = settledForecasts.Average(forecast => forecast.OutcomeOccurred == true ? 1.0 : 0.0);
            stats.Uncertainty = overallObservedRate * (1.0 - overallObservedRate);
            stats.ForecastHitRate = overallObservedRate;
            stats.ConfidenceBandStats = BuildConfidenceBandStats(settledForecasts);
            stats.LeagueSegmentStats = BuildLeagueSegmentStats(settledForecasts);
            stats.SourceSegmentStats = BuildSourceSegmentStats(settledForecasts);

            stats.ForecastMarketStats = settledForecasts
                .GroupBy(forecast => forecast.Market)
                .OrderBy(group => group.Key)
                .Select(BuildMarketStat)
                .ToList();
            stats.FeatureDiagnostics = BuildFeatureDiagnostics(settledForecasts);
        }

        stats.BettingPerformance = BuildBettingPerformance(
            completedPredictions,
            publishedPredictions,
            oddsSnapshots);

        return stats;
    }

    private static List<ForecastFeatureDiagnostic> BuildFeatureDiagnostics(IReadOnlyCollection<ForecastObservation> settledForecasts)
    {
        return settledForecasts
            .OrderByDescending(forecast => forecast.MatchDateTime ?? DateTime.MinValue)
            .ThenByDescending(forecast => forecast.CreatedAt)
            .Take(MaxFeatureDiagnostics)
            .Select(forecast => new ForecastFeatureDiagnostic
            {
                MatchDateTime = forecast.MatchDateTime,
                League = forecast.League,
                HomeTeam = forecast.HomeTeam,
                AwayTeam = forecast.AwayTeam,
                MarketName = forecast.Market.ToDisplayName(),
                PredictedOutcome = forecast.PredictedOutcome,
                RawProbability = forecast.RawProbability,
                CalibratedProbability = forecast.CalibratedProbability,
                OutcomeOccurred = forecast.OutcomeOccurred,
                StatisticalSignalApplied = ReadStatisticalSignalApplied(forecast.FeatureContributionsJson),
                Contributions = ParseFeatureContributions(forecast.FeatureContributionsJson)
            })
            .ToList();
    }

    private static bool ReadStatisticalSignalApplied(string? featureContributionsJson)
    {
        if (string.IsNullOrWhiteSpace(featureContributionsJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(featureContributionsJson);
            return document.RootElement.TryGetProperty("statisticalSignalApplied", out var applied) &&
                   applied.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static List<FeatureContributionItem> ParseFeatureContributions(string? featureContributionsJson)
    {
        var items = new List<FeatureContributionItem>();
        if (string.IsNullOrWhiteSpace(featureContributionsJson))
        {
            return items;
        }

        try
        {
            using var document = JsonDocument.Parse(featureContributionsJson);
            var root = document.RootElement;
            AppendContributionGroup(items, root, "sourceSignals", "Market");
            AppendContributionGroup(items, root, "modelOutputs", "Model");
            AppendContributionGroup(items, root, "statisticalSignal", "Statistical");
        }
        catch (JsonException)
        {
            // Diagnostics are best-effort; malformed JSON simply yields no rows.
        }

        return items;
    }

    private static void AppendContributionGroup(
        List<FeatureContributionItem> items,
        JsonElement root,
        string propertyName,
        string groupLabel)
    {
        if (!root.TryGetProperty(propertyName, out var group) || group.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in group.EnumerateObject())
        {
            double? value = property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var parsed)
                ? parsed
                : null;
            items.Add(new FeatureContributionItem
            {
                Group = groupLabel,
                Label = property.Name,
                Value = value
            });
        }
    }

    private static BettingPerformanceStats BuildBettingPerformance(
        IReadOnlyCollection<Prediction> completedPredictions,
        IReadOnlyCollection<Prediction> publishedPredictions,
        IEnumerable<PredictionOddsSnapshot>? oddsSnapshots)
    {
        var performance = new BettingPerformanceStats { KellyFraction = KellyFraction };
        if (oddsSnapshots is null)
        {
            return performance;
        }

        var snapshotsByPrediction = oddsSnapshots
            .Where(snapshot => snapshot.SnapshotKind is PredictionOddsSnapshotKind.Publish or PredictionOddsSnapshotKind.Close)
            .GroupBy(snapshot => snapshot.PredictionId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var snapshotsByFixtureMarket = new Dictionary<(string FixtureKey, string Category), List<PredictionOddsSnapshot>>();
        foreach (var prediction in publishedPredictions)
        {
            if (!snapshotsByPrediction.TryGetValue(prediction.Id, out var snapshots))
            {
                continue;
            }

            var identity = (BuildPredictionFixtureKey(prediction), prediction.PredictionCategory);
            if (!snapshotsByFixtureMarket.TryGetValue(identity, out var grouped))
            {
                grouped = [];
                snapshotsByFixtureMarket[identity] = grouped;
            }

            grouped.AddRange(snapshots);
        }

        var bets = new List<BetRecord>();
        foreach (var prediction in completedPredictions)
        {
            if (!snapshotsByPrediction.TryGetValue(prediction.Id, out var ownSnapshots))
            {
                continue;
            }

            var publishOdds = ResolveSnapshotOdds(ownSnapshots, prediction, PredictionOddsSnapshotKind.Publish);
            if (publishOdds is not > 1.0)
            {
                continue;
            }

            var identity = (BuildPredictionFixtureKey(prediction), prediction.PredictionCategory);
            var closeSnapshots = snapshotsByFixtureMarket.TryGetValue(identity, out var fixtureSnapshots)
                ? fixtureSnapshots
                : ownSnapshots;
            var closeOdds = ResolveSnapshotOdds(closeSnapshots, prediction, PredictionOddsSnapshotKind.Close);
            var won = IsPredictionCorrectForAnalytics(prediction);
            var modelProbability = prediction.ConfidenceScore.HasValue
                ? (double)Math.Clamp(prediction.ConfidenceScore.Value, 0m, 1m)
                : 0.0;

            bets.Add(new BetRecord(
                MarketKey: prediction.PredictionCategory,
                DecimalOdds: publishOdds.Value,
                Won: won,
                ModelProbability: modelProbability,
                Clv: BetPricingMath.CalculateClosingLineValuePercent(publishOdds, closeOdds),
                KickoffUtc: prediction.MatchDateTime,
                CreatedAtUtc: prediction.CreatedAt));
        }

        if (bets.Count == 0)
        {
            return performance;
        }

        var chronologicalBets = bets
            .OrderBy(bet => bet.KickoffUtc ?? DateTime.MaxValue)
            .ThenBy(bet => bet.CreatedAtUtc)
            .ToList();
        PopulateAggregate(performance, chronologicalBets);
        performance.Markets = bets
            .GroupBy(bet => bet.MarketKey)
            .Select(group =>
            {
                var marketBets = group.ToList();
                var clvSamples = marketBets.Where(bet => bet.Clv.HasValue).Select(bet => bet.Clv!.Value).ToList();
                var staked = marketBets.Count;
                var net = marketBets.Sum(bet => bet.FlatProfit);
                return new MarketBettingStat
                {
                    MarketKey = group.Key,
                    MarketName = MapCategoryToDisplayName(group.Key),
                    SettledBetCount = marketBets.Count,
                    WinningBetCount = marketBets.Count(bet => bet.Won),
                    WinRate = marketBets.Count(bet => bet.Won) / (double)marketBets.Count,
                    NetProfitUnits = net,
                    RoiPercent = staked > 0 ? net / staked : 0.0,
                    AverageOdds = marketBets.Average(bet => bet.DecimalOdds),
                    ClosingLineSamples = clvSamples.Count,
                    AverageClosingLineValuePercent = clvSamples.Count > 0 ? clvSamples.Average() : 0.0
                };
            })
            .OrderByDescending(stat => stat.SettledBetCount)
            .ThenBy(stat => stat.MarketName)
            .ToList();

        return performance;
    }

    private static void PopulateAggregate(BettingPerformanceStats performance, IReadOnlyCollection<BetRecord> bets)
    {
        var flatReturns = bets.Select(bet => bet.FlatProfit).ToList();
        performance.SettledBetCount = bets.Count;
        performance.WinningBetCount = bets.Count(bet => bet.Won);
        performance.WinRate = performance.WinningBetCount / (double)bets.Count;
        performance.TotalStakedUnits = bets.Count;
        performance.NetProfitUnits = flatReturns.Sum();
        performance.RoiPercent = performance.TotalStakedUnits > 0 ? performance.NetProfitUnits / performance.TotalStakedUnits : 0.0;
        performance.YieldPercent = performance.RoiPercent;
        performance.MaxDrawdownUnits = CalculateMaxDrawdown(flatReturns);
        performance.AverageOdds = bets.Average(bet => bet.DecimalOdds);

        var kellyBets = bets.Where(bet => bet.KellyStake > 0).ToList();
        performance.KellyBetCount = kellyBets.Count;
        performance.KellyStakedUnits = kellyBets.Sum(bet => bet.KellyStake);
        performance.KellyNetProfitUnits = kellyBets.Sum(bet => bet.KellyProfit);
        performance.KellyRoiPercent = performance.KellyStakedUnits > 0
            ? performance.KellyNetProfitUnits / performance.KellyStakedUnits
            : 0.0;

        var clvSamples = bets.Where(bet => bet.Clv.HasValue).Select(bet => bet.Clv!.Value).ToList();
        performance.ClosingLineSamples = clvSamples.Count;
        performance.AverageClosingLineValuePercent = clvSamples.Count > 0 ? clvSamples.Average() : 0.0;
        performance.BeatCloseRate = clvSamples.Count > 0
            ? clvSamples.Count(value => value > 0) / (double)clvSamples.Count
            : 0.0;
    }

    private static double? ResolveSnapshotOdds(
        IReadOnlyCollection<PredictionOddsSnapshot> snapshots,
        Prediction prediction,
        PredictionOddsSnapshotKind kind)
    {
        var matching = snapshots.Where(snapshot => snapshot.SnapshotKind == kind).ToList();
        if (matching.Count == 0)
        {
            return null;
        }

        // Prefer the snapshot whose outcome matches the published pick; otherwise fall back to the first.
        var preferred = matching.FirstOrDefault(snapshot =>
            OutcomesMatch(snapshot.Outcome, prediction.PredictedOutcome)) ?? matching[0];
        return preferred.DecimalOdds > 1.0 ? preferred.DecimalOdds : null;
    }

    private static double CalculateMaxDrawdown(IEnumerable<double> returns)
    {
        var equity = 0.0;
        var peak = 0.0;
        var maxDrawdown = 0.0;
        foreach (var value in returns)
        {
            equity += value;
            peak = Math.Max(peak, equity);
            maxDrawdown = Math.Max(maxDrawdown, peak - equity);
        }

        return maxDrawdown;
    }

    private sealed record BetRecord(
        string MarketKey,
        double DecimalOdds,
        bool Won,
        double ModelProbability,
        double? Clv,
        DateTime? KickoffUtc,
        DateTime CreatedAtUtc)
    {
        public double FlatProfit => Won ? DecimalOdds - 1.0 : -1.0;

        public double KellyStake =>
            BetPricingMath.CalculateFractionalKellyStakeFraction(ModelProbability, DecimalOdds, KellyFraction);

        public double KellyProfit => Won ? KellyStake * (DecimalOdds - 1.0) : -KellyStake;
    }

    private static string MapCategoryToDisplayName(string category)
    {
        return category switch
        {
            "BothTeamsScore" => PredictionMarket.BothTeamsScore.ToDisplayName(),
            "Over2.5Goals" => PredictionMarket.Over25Goals.ToDisplayName(),
            "Under2.5Goals" => PredictionMarket.Under25Goals.ToDisplayName(),
            "StraightWin" => PredictionMarket.StraightWin.ToDisplayName(),
            "Draw" => PredictionMarket.Draw.ToDisplayName(),
            _ => category
        };
    }

    private static string BuildPredictionFixtureKey(Prediction prediction)
    {
        if (!string.IsNullOrWhiteSpace(prediction.FixtureKey))
        {
            return prediction.FixtureKey.Trim();
        }

        return string.Join(
            "|",
            prediction.MatchLocalDate.ToString("yyyy-MM-dd"),
            (prediction.League ?? string.Empty).Trim().ToLowerInvariant(),
            (prediction.HomeTeam ?? string.Empty).Trim().ToLowerInvariant(),
            (prediction.AwayTeam ?? string.Empty).Trim().ToLowerInvariant());
    }

    private static double CalculateExpectedCalibrationError(IEnumerable<(double Probability, bool Outcome)> inputs)
    {
        var list = inputs.ToList();
        if (list.Count == 0)
        {
            return 0.0;
        }

        var ece = 0.0;
        foreach (var group in list.GroupBy(item => GetBucketStart(item.Probability)))
        {
            var count = group.Count();
            var averageProbability = group.Average(item => Math.Clamp(item.Probability, 0.0, 1.0));
            var observedFrequency = group.Average(item => item.Outcome ? 1.0 : 0.0);
            ece += (count / (double)list.Count) * Math.Abs(averageProbability - observedFrequency);
        }

        return ece;
    }

    private static bool IsActiveAnalyticsPrediction(Prediction prediction)
    {
        return !string.Equals(prediction.PredictionCategory, "Draw", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsActiveAnalyticsForecast(ForecastObservation forecast)
    {
        return forecast.Market != PredictionMarket.Draw;
    }

    private static bool IsPredictionCompletedForAnalytics(Prediction prediction)
    {
        if (prediction.IsLive && !HasStoredActualOutcome(prediction))
        {
            return false;
        }

        return ResolvePredictionActualOutcome(prediction) is not null;
    }

    private static bool IsPredictionCorrectForAnalytics(Prediction prediction)
    {
        if (TryParseScore(prediction.ActualScore, out var homeGoals, out var awayGoals))
        {
            return prediction.PredictionCategory switch
            {
                "BothTeamsScore" => DoesBttsPredictionMatch(prediction.PredictedOutcome, homeGoals, awayGoals),
                "Over2.5Goals" => DoesOverPredictionMatch(prediction.PredictedOutcome, homeGoals, awayGoals),
                "Under2.5Goals" => DoesOverPredictionMatch(prediction.PredictedOutcome, homeGoals, awayGoals),
                "Draw" => DoesDrawPredictionMatch(prediction.PredictedOutcome, homeGoals, awayGoals),
                "StraightWin" => DoesStraightWinPredictionMatch(prediction.PredictedOutcome, homeGoals, awayGoals),
                _ => OutcomesMatch(prediction.PredictedOutcome, ResolvePredictionActualOutcome(prediction))
            };
        }

        return OutcomesMatch(prediction.PredictedOutcome, ResolvePredictionActualOutcome(prediction));
    }

    private static bool HasStoredActualOutcome(Prediction prediction)
    {
        return !string.IsNullOrWhiteSpace(prediction.ActualOutcome) &&
               !string.Equals(prediction.ActualOutcome, "Unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolvePredictionActualOutcome(Prediction prediction)
    {
        if (HasStoredActualOutcome(prediction))
        {
            return prediction.ActualOutcome;
        }

        if (prediction.IsLive)
        {
            return null;
        }

        if (!TryParseScore(prediction.ActualScore, out var homeGoals, out var awayGoals))
        {
            return null;
        }

        return prediction.PredictionCategory switch
        {
            "BothTeamsScore" => homeGoals > 0 && awayGoals > 0 ? "BTTS" : "No BTTS",
            "Over2.5Goals" => homeGoals + awayGoals > 2 ? "Over 2.5" : "Under 2.5",
            "Under2.5Goals" => homeGoals + awayGoals > 2 ? "Over 2.5" : "Under 2.5",
            "Draw" => homeGoals == awayGoals ? "Draw" : "Not Draw",
            "StraightWin" => homeGoals > awayGoals ? "Home Win" : awayGoals > homeGoals ? "Away Win" : "Draw",
            _ => null
        };
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
            HitRate = settled.Average(forecast => forecast.OutcomeOccurred == true ? 1.0 : 0.0),
            LogLoss = settled.Average(forecast => BinaryLogLoss(forecast.CalibratedProbability, forecast.OutcomeOccurred!.Value)),
            Precision = settled.Count > 0 ? settled.Count(forecast => forecast.OutcomeOccurred == true) / (double)settled.Count : 0.0,
            // Recall against all opportunities is not observable for a picks-only set;
            // report coverage-of-picks (same as precision) instead of a hardcoded 1.0
            // so F1 is not artificially inflated.
            Recall = settled.Count > 0 ? settled.Count(forecast => forecast.OutcomeOccurred == true) / (double)settled.Count : 0.0,
            F1Score = settled.Count > 0
                ? CalculateF1(
                    settled.Count(forecast => forecast.OutcomeOccurred == true) / (double)settled.Count,
                    settled.Count(forecast => forecast.OutcomeOccurred == true) / (double)settled.Count)
                : 0.0,
            RawBrierScore = rawInputs.Count > 0 ? rawInputs.Average(input => SquaredError(input.Probability, input.Outcome)) : 0.0,
            CalibratedBrierScore = calibratedInputs.Count > 0 ? calibratedInputs.Average(input => SquaredError(input.Probability, input.Outcome)) : 0.0,
            RawExpectedCalibrationError = CalculateExpectedCalibrationError(rawInputs),
            CalibratedExpectedCalibrationError = CalculateExpectedCalibrationError(calibratedInputs),
            RawDecomposition = BuildDecomposition(rawInputs),
            CalibratedDecomposition = BuildDecomposition(calibratedInputs),
            RawReliabilityCurve = BuildReliabilityCurve(rawInputs),
            CalibratedReliabilityCurve = BuildReliabilityCurve(calibratedInputs),
            CalibratorEraStats = BuildEraStats(
                settled,
                forecast => NormalizeCalibrator(forecast.CalibratorUsed),
                ["Isotonic", "Beta", "Bucket", "Unknown"]),
            ThresholdEraStats = BuildEraStats(
                settled.Where(forecast => forecast.IsPublished),
                forecast => NormalizeThresholdSource(forecast.ThresholdSource),
                ["Configured", "Tuned", "Unknown"])
        };
    }

    private static List<ConfidenceBandStat> BuildConfidenceBandStats(IReadOnlyCollection<ForecastObservation> forecasts)
    {
        return forecasts
            .GroupBy(forecast => GetConfidenceBandStart(forecast.CalibratedProbability))
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var items = group.ToList();
                // Within a confidence band every forecast is a "positive" pick, so precision is the
                // realized hit rate. Recall against the full opportunity set is not observable for a
                // picks-only sample, so we report coverage-of-picks (equal to precision) for a
                // consistent, non-inflated F1 - mirroring the per-market convention above.
                var hitRate = items.Average(item => item.OutcomeOccurred == true ? 1.0 : 0.0);
                return new ConfidenceBandStat
                {
                    MinProbability = group.Key,
                    MaxProbability = Math.Min(group.Key + ConfidenceBandSize, 1.0),
                    SampleCount = items.Count,
                    HitRate = hitRate,
                    AverageProbability = items.Average(item => item.CalibratedProbability),
                    BrierScore = items.Average(item => SquaredError(item.CalibratedProbability, item.OutcomeOccurred!.Value)),
                    LogLoss = items.Average(item => BinaryLogLoss(item.CalibratedProbability, item.OutcomeOccurred!.Value)),
                    Precision = hitRate,
                    Recall = hitRate,
                    F1Score = CalculateF1(hitRate, hitRate)
                };
            })
            .ToList();
    }

    private static List<LeagueSegmentStat> BuildLeagueSegmentStats(IReadOnlyCollection<ForecastObservation> forecasts)
    {
        return forecasts
            .GroupBy(forecast => string.IsNullOrWhiteSpace(forecast.League) ? "Unknown" : forecast.League.Trim())
            .Select(group =>
            {
                var items = group.ToList();
                var outcomes = items
                    .Select(item => (Probability: item.CalibratedProbability, Outcome: item.OutcomeOccurred!.Value))
                    .ToList();
                var hitRate = items.Average(item => item.OutcomeOccurred == true ? 1.0 : 0.0);
                return new LeagueSegmentStat
                {
                    League = group.Key,
                    SampleCount = items.Count,
                    HitRate = hitRate,
                    BrierScore = items.Average(item => SquaredError(item.CalibratedProbability, item.OutcomeOccurred!.Value)),
                    LogLoss = items.Average(item => BinaryLogLoss(item.CalibratedProbability, item.OutcomeOccurred!.Value)),
                    ExpectedCalibrationError = CalculateExpectedCalibrationError(outcomes),
                    Uncertainty = hitRate * (1.0 - hitRate)
                };
            })
            .OrderByDescending(stat => stat.SampleCount)
            .ThenBy(stat => stat.League)
            .ToList();
    }

    private static List<SourceSegmentStat> BuildSourceSegmentStats(IReadOnlyCollection<ForecastObservation> forecasts)
    {
        return forecasts
            .GroupBy(forecast => string.IsNullOrWhiteSpace(forecast.CalibratorUsed) ? "Unknown" : forecast.CalibratorUsed.Trim())
            .Select(group =>
            {
                var items = group.ToList();
                var outcomes = items
                    .Select(item => (Probability: item.CalibratedProbability, Outcome: item.OutcomeOccurred!.Value))
                    .ToList();
                var hitRate = items.Average(item => item.OutcomeOccurred == true ? 1.0 : 0.0);
                return new SourceSegmentStat
                {
                    SourceName = group.Key,
                    SampleCount = items.Count,
                    HitRate = hitRate,
                    BrierScore = items.Average(item => SquaredError(item.CalibratedProbability, item.OutcomeOccurred!.Value)),
                    LogLoss = items.Average(item => BinaryLogLoss(item.CalibratedProbability, item.OutcomeOccurred!.Value)),
                    ExpectedCalibrationError = CalculateExpectedCalibrationError(outcomes),
                    Uncertainty = hitRate * (1.0 - hitRate)
                };
            })
            .OrderByDescending(stat => stat.SampleCount)
            .ThenBy(stat => stat.SourceName)
            .ToList();
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

    private static double BinaryLogLoss(double probability, bool outcome)
    {
        var p = Math.Clamp(probability, 1e-6, 1.0 - 1e-6);
        return outcome ? -Math.Log(p) : -Math.Log(1.0 - p);
    }

    private static double CalculatePrecision(IEnumerable<Prediction> predictions)
    {
        var list = predictions.ToList();
        if (list.Count == 0)
        {
            return 0.0;
        }

        var truePositives = list.Count(IsPredictionCorrectForAnalytics);
        return truePositives / (double)list.Count;
    }

    private static double CalculateRecall(IEnumerable<Prediction> predictions)
    {
        var list = predictions.ToList();
        if (list.Count == 0)
        {
            return 0.0;
        }

        // For a picks-only set we treat recall as coverage of selected opportunities.
        return list.Count(IsPredictionCorrectForAnalytics) / (double)list.Count;
    }

    private static double CalculateF1(double precision, double recall)
    {
        if (precision <= 0 || recall <= 0)
        {
            return 0.0;
        }

        return (2.0 * precision * recall) / (precision + recall);
    }

    private static double GetBucketStart(double probability)
    {
        var clamped = Math.Clamp(probability, 0.0, 0.999999);
        return Math.Floor(clamped / BucketSize) * BucketSize;
    }

    private static double GetConfidenceBandStart(double probability)
    {
        var clamped = Math.Clamp(probability, 0.0, 0.999999);
        return Math.Floor(clamped / ConfidenceBandSize) * ConfidenceBandSize;
    }

    private static string NormalizeCalibrator(string? calibratorUsed)
    {
        if (string.IsNullOrWhiteSpace(calibratorUsed) || calibratorUsed.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return "Unknown";
        }

        if (calibratorUsed.Equals("Isotonic", StringComparison.OrdinalIgnoreCase))
        {
            return "Isotonic";
        }

        if (calibratorUsed.Equals("Beta", StringComparison.OrdinalIgnoreCase))
        {
            return "Beta";
        }

        if (calibratorUsed.Equals("Bucket", StringComparison.OrdinalIgnoreCase))
        {
            return "Bucket";
        }

        return calibratorUsed.Trim();
    }

    private static string NormalizeThresholdSource(string? thresholdSource)
    {
        if (string.IsNullOrWhiteSpace(thresholdSource) || thresholdSource.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return "Unknown";
        }

        return thresholdSource.Equals("Tuned", StringComparison.OrdinalIgnoreCase) ? "Tuned" : "Configured";
    }

    private static bool TryParseScore(string? score, out int homeGoals, out int awayGoals)
    {
        homeGoals = 0;
        awayGoals = 0;

        if (string.IsNullOrWhiteSpace(score))
        {
            return false;
        }

        var normalized = score.Replace("–", "-").Replace("—", "-").Trim();
        var parts = normalized.Contains(':')
            ? normalized.Split(':', StringSplitOptions.TrimEntries)
            : normalized.Split('-', StringSplitOptions.TrimEntries);

        return parts.Length == 2 &&
               int.TryParse(parts[0], out homeGoals) &&
               int.TryParse(parts[1], out awayGoals);
    }

    private static bool DoesBttsPredictionMatch(string? predictedOutcome, int homeGoals, int awayGoals)
    {
        var bothTeamsScored = homeGoals > 0 && awayGoals > 0;

        return NormalizeOutcome(predictedOutcome) switch
        {
            "btts" or "yes" or "gg" => bothTeamsScored,
            "no btts" or "no" or "ng" => !bothTeamsScored,
            // Unknown labels must never count as wins; that inflates accuracy metrics.
            _ => false
        };
    }

    private static bool DoesOverPredictionMatch(string? predictedOutcome, int homeGoals, int awayGoals)
    {
        var isOver = homeGoals + awayGoals > 2;

        return NormalizeOutcome(predictedOutcome) switch
        {
            "over" or "over 2.5" or "over2.5" => isOver,
            "under" or "under 2.5" or "under2.5" => !isOver,
            _ => false
        };
    }

    private static bool DoesDrawPredictionMatch(string? predictedOutcome, int homeGoals, int awayGoals)
    {
        var isDraw = homeGoals == awayGoals;

        return NormalizeOutcome(predictedOutcome) switch
        {
            "draw" => isDraw,
            "not draw" => !isDraw,
            _ => false
        };
    }

    private static bool DoesStraightWinPredictionMatch(string? predictedOutcome, int homeGoals, int awayGoals)
    {
        return NormalizeOutcome(predictedOutcome) switch
        {
            "home win" or "home" or "1" => homeGoals > awayGoals,
            "away win" or "away" or "2" => awayGoals > homeGoals,
            "draw" or "x" => homeGoals == awayGoals,
            _ => false
        };
    }

    private static bool OutcomesMatch(string? predictedOutcome, string? actualOutcome)
    {
        var normalizedPredicted = NormalizeOutcome(predictedOutcome);
        var normalizedActual = NormalizeOutcome(actualOutcome);

        return normalizedPredicted.Length > 0 &&
               normalizedActual.Length > 0 &&
               normalizedPredicted == normalizedActual;
    }

    private static string NormalizeOutcome(string? outcome)
    {
        if (string.IsNullOrWhiteSpace(outcome))
        {
            return string.Empty;
        }

        return System.Text.RegularExpressions.Regex.Replace(
            outcome.Trim().ToLowerInvariant(),
            @"\s+",
            " ");
    }
}
