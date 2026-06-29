namespace MatchPredictor.Infrastructure.Statistics.Backtesting;

/// <summary>
/// Generic walk-forward (rolling-origin) backtesting harness. It repeatedly trains a
/// model on everything known <em>before</em> a test window and evaluates it on the
/// fixtures inside that window, so there is never any look-ahead leakage. Aggregating
/// the out-of-sample samples yields an honest estimate of live performance and is the
/// gate used for model promotion.
/// </summary>
public static class WalkForwardBacktester
{
    public static WalkForwardReport Run<TItem, TModel>(
        IReadOnlyCollection<TItem> items,
        Func<TItem, DateTime> dateSelector,
        Func<IReadOnlyList<TItem>, TModel> train,
        Func<TModel, TItem, BacktestSample?> predict,
        int minTrainingSize,
        TimeSpan testWindow,
        double betThreshold = 0.5)
    {
        var ordered = items.OrderBy(dateSelector).ToList();
        var samples = new List<BacktestSample>();
        var foldCount = 0;

        if (ordered.Count == 0 || minTrainingSize <= 0 || testWindow <= TimeSpan.Zero)
        {
            return new WalkForwardReport(BacktestMetrics.Empty, 0);
        }

        // The first test window starts immediately after the minTrainingSize-th item.
        var windowStart = dateSelector(ordered[Math.Min(minTrainingSize, ordered.Count) - 1]);
        var lastDate = dateSelector(ordered[^1]);

        while (windowStart <= lastDate)
        {
            var windowEnd = windowStart + testWindow;

            var training = ordered.Where(item => dateSelector(item) < windowStart).ToList();
            var testing = ordered
                .Where(item => dateSelector(item) >= windowStart && dateSelector(item) < windowEnd)
                .ToList();

            if (training.Count >= minTrainingSize && testing.Count > 0)
            {
                var model = train(training);
                foldCount++;
                foreach (var item in testing)
                {
                    var sample = predict(model, item);
                    if (sample is not null)
                    {
                        samples.Add(sample);
                    }
                }
            }

            windowStart = windowEnd;
        }

        return new WalkForwardReport(BacktestEvaluator.Evaluate(samples, betThreshold), foldCount);
    }
}

public sealed record WalkForwardReport(BacktestMetrics Metrics, int FoldCount);
