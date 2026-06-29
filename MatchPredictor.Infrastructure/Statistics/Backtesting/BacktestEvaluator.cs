namespace MatchPredictor.Infrastructure.Statistics.Backtesting;

/// <summary>
/// Computes accuracy, calibration and profitability metrics for a set of
/// out-of-sample predictions. Pure function — no I/O — so it is fully deterministic
/// and testable.
/// </summary>
public static class BacktestEvaluator
{
    public static BacktestMetrics Evaluate(
        IReadOnlyList<BacktestSample> samples,
        double betThreshold = 0.5,
        double kellyFraction = 0.25,
        int calibrationBuckets = 10)
    {
        if (samples is null || samples.Count == 0)
        {
            return BacktestMetrics.Empty;
        }

        var brier = 0.0;
        var logLoss = 0.0;
        var correct = 0;

        foreach (var sample in samples)
        {
            var probability = Math.Clamp(sample.Probability, 0.0, 1.0);
            var outcome = sample.Outcome ? 1.0 : 0.0;

            brier += Math.Pow(probability - outcome, 2);
            var clamped = Math.Clamp(probability, 1e-6, 1.0 - 1e-6);
            logLoss += sample.Outcome ? -Math.Log(clamped) : -Math.Log(1.0 - clamped);

            var predictedPositive = probability >= 0.5;
            if (predictedPositive == sample.Outcome)
            {
                correct++;
            }
        }

        var ece = ComputeEce(samples, calibrationBuckets);
        var (betCount, hitRate, roi, clv, maxDrawdown, kellyRoi) = ComputeBettingMetrics(samples, betThreshold, kellyFraction);

        return new BacktestMetrics
        {
            SampleCount = samples.Count,
            BetCount = betCount,
            Accuracy = correct / (double)samples.Count,
            Brier = brier / samples.Count,
            LogLoss = logLoss / samples.Count,
            Ece = ece,
            HitRate = hitRate,
            Roi = roi,
            Yield = roi,
            Clv = clv,
            MaxDrawdown = maxDrawdown,
            KellyRoi = kellyRoi
        };
    }

    private static double ComputeEce(IReadOnlyList<BacktestSample> samples, int buckets)
    {
        if (buckets <= 0)
        {
            return 0.0;
        }

        var bucketCounts = new int[buckets];
        var bucketProbabilitySum = new double[buckets];
        var bucketOutcomeSum = new double[buckets];

        foreach (var sample in samples)
        {
            var probability = Math.Clamp(sample.Probability, 0.0, 0.999999);
            var index = Math.Min((int)(probability * buckets), buckets - 1);
            bucketCounts[index]++;
            bucketProbabilitySum[index] += probability;
            bucketOutcomeSum[index] += sample.Outcome ? 1.0 : 0.0;
        }

        var ece = 0.0;
        for (var i = 0; i < buckets; i++)
        {
            if (bucketCounts[i] == 0)
            {
                continue;
            }

            var averageProbability = bucketProbabilitySum[i] / bucketCounts[i];
            var observedFrequency = bucketOutcomeSum[i] / bucketCounts[i];
            var weight = bucketCounts[i] / (double)samples.Count;
            ece += weight * Math.Abs(averageProbability - observedFrequency);
        }

        return ece;
    }

    private static (int BetCount, double HitRate, double Roi, double Clv, double MaxDrawdown, double KellyRoi)
        ComputeBettingMetrics(IReadOnlyList<BacktestSample> samples, double betThreshold, double kellyFraction)
    {
        var betCount = 0;
        var wins = 0;
        var staked = 0.0;
        var profit = 0.0;
        var clvSum = 0.0;
        var clvCount = 0;

        var cumulative = 0.0;
        var peak = 0.0;
        var maxDrawdown = 0.0;

        var bankroll = 1.0;

        foreach (var sample in samples.OrderBy(s => s.DateUtc ?? DateTime.MinValue))
        {
            if (sample.Probability < betThreshold || sample.DecimalOdds is null || sample.DecimalOdds <= 1.0)
            {
                continue;
            }

            var odds = sample.DecimalOdds.Value;
            betCount++;
            staked += 1.0;

            var pnl = sample.Outcome ? odds - 1.0 : -1.0;
            profit += pnl;
            if (sample.Outcome)
            {
                wins++;
            }

            cumulative += pnl;
            peak = Math.Max(peak, cumulative);
            maxDrawdown = Math.Max(maxDrawdown, peak - cumulative);

            if (sample.CloseDecimalOdds is > 1.0)
            {
                clvSum += (odds / sample.CloseDecimalOdds.Value) - 1.0;
                clvCount++;
            }

            // Fractional Kelly on the model's own probability estimate.
            var edge = (sample.Probability * (odds - 1.0)) - (1.0 - sample.Probability);
            if (edge > 0)
            {
                var fullKelly = edge / (odds - 1.0);
                var stakeFraction = Math.Clamp(fullKelly * kellyFraction, 0.0, 0.5);
                var kellyStake = bankroll * stakeFraction;
                bankroll += sample.Outcome ? kellyStake * (odds - 1.0) : -kellyStake;
            }
        }

        if (betCount == 0)
        {
            return (0, 0.0, 0.0, 0.0, 0.0, 0.0);
        }

        return (
            betCount,
            wins / (double)betCount,
            profit / staked,
            clvCount > 0 ? clvSum / clvCount : 0.0,
            maxDrawdown,
            bankroll - 1.0);
    }
}
