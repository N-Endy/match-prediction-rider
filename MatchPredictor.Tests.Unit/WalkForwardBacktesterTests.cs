using MatchPredictor.Infrastructure.Statistics.Backtesting;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class WalkForwardBacktesterTests
{
    private sealed record Item(DateTime Date, bool Outcome);

    [Fact]
    public void Run_NeverTrainsOnFutureData()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var items = Enumerable.Range(0, 60)
            .Select(i => new Item(start.AddDays(i), i % 3 == 0))
            .ToList();

        var leakageViolations = 0;

        var report = WalkForwardBacktester.Run<Item, DateTime>(
            items,
            item => item.Date,
            // The "model" is simply the latest training date observed.
            training => training.Max(t => t.Date),
            (latestTrainingDate, item) =>
            {
                if (latestTrainingDate >= item.Date)
                {
                    leakageViolations++;
                }

                return new BacktestSample(item.Outcome ? 0.6 : 0.4, item.Outcome, DateUtc: item.Date);
            },
            minTrainingSize: 15,
            testWindow: TimeSpan.FromDays(5));

        Assert.Equal(0, leakageViolations);
        Assert.True(report.FoldCount > 0);
        Assert.True(report.Metrics.SampleCount > 0);
    }

    [Fact]
    public void Run_AggregatesOutOfSampleMetrics()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // Perfectly predictable signal: a model that learns the base rate predicts well.
        var items = Enumerable.Range(0, 40)
            .Select(i => new Item(start.AddDays(i), true))
            .ToList();

        var report = WalkForwardBacktester.Run<Item, double>(
            items,
            item => item.Date,
            _ => 1.0,
            (probability, item) => new BacktestSample(probability, item.Outcome, DateUtc: item.Date),
            minTrainingSize: 10,
            testWindow: TimeSpan.FromDays(5));

        Assert.True(report.Metrics.SampleCount > 0);
        Assert.Equal(1.0, report.Metrics.Accuracy, 6);
    }

    [Fact]
    public void Run_WithInsufficientData_ReturnsEmpty()
    {
        var report = WalkForwardBacktester.Run<Item, double>(
            [],
            item => item.Date,
            _ => 0.0,
            (_, _) => null,
            minTrainingSize: 10,
            testWindow: TimeSpan.FromDays(5));

        Assert.Equal(0, report.FoldCount);
        Assert.Equal(0, report.Metrics.SampleCount);
    }
}
