using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Statistics.Backtesting;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class BacktestEvaluatorTests
{
    [Fact]
    public void Evaluate_PerfectPredictions_ScorePerfectly()
    {
        var samples = new List<BacktestSample>
        {
            new(1.0, true),
            new(0.0, false),
            new(1.0, true),
            new(0.0, false)
        };

        var metrics = BacktestEvaluator.Evaluate(samples);

        Assert.Equal(1.0, metrics.Accuracy, 6);
        Assert.Equal(0.0, metrics.Brier, 6);
        Assert.True(metrics.LogLoss < 1e-3);
        // ECE is ~0 (a tiny residual comes from clamping prob=1.0 away from the bin edge).
        Assert.True(metrics.Ece < 1e-5);
    }

    [Fact]
    public void Evaluate_ComputesBrierAccuracyAndRoi()
    {
        // 10 bets at prob 0.6 / odds 2.0: 6 winners, 4 losers.
        var samples = new List<BacktestSample>();
        for (var i = 0; i < 10; i++)
        {
            samples.Add(new BacktestSample(0.6, i < 6, DecimalOdds: 2.0));
        }

        var metrics = BacktestEvaluator.Evaluate(samples, betThreshold: 0.5);

        Assert.Equal(10, metrics.BetCount);
        Assert.Equal(0.6, metrics.Accuracy, 6);
        Assert.Equal(0.24, metrics.Brier, 6);
        Assert.Equal(0.6, metrics.HitRate, 6);
        Assert.Equal(0.2, metrics.Roi, 6);
        Assert.Equal(0.0, metrics.Ece, 6);
    }

    [Fact]
    public void Evaluate_ComputesClosingLineValue()
    {
        var samples = new List<BacktestSample>
        {
            new(0.6, true, DecimalOdds: 2.0, CloseDecimalOdds: 1.9),
            new(0.6, false, DecimalOdds: 2.0, CloseDecimalOdds: 1.9)
        };

        var metrics = BacktestEvaluator.Evaluate(samples);

        // Took 2.0 versus a 1.9 close => positive CLV of 2.0/1.9 - 1.
        Assert.Equal((2.0 / 1.9) - 1.0, metrics.Clv, 6);
    }

    [Fact]
    public void Evaluate_ComputesMaxDrawdown()
    {
        var baseDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var samples = new List<BacktestSample>
        {
            new(0.6, false, DecimalOdds: 2.0, DateUtc: baseDate),
            new(0.6, false, DecimalOdds: 2.0, DateUtc: baseDate.AddDays(1)),
            new(0.6, true, DecimalOdds: 2.0, DateUtc: baseDate.AddDays(2)),
            new(0.6, true, DecimalOdds: 2.0, DateUtc: baseDate.AddDays(3))
        };

        var metrics = BacktestEvaluator.Evaluate(samples);

        // Cumulative P&L: -1, -2, -1, 0 => peak 0, worst trough -2 => drawdown 2.
        Assert.Equal(2.0, metrics.MaxDrawdown, 6);
    }

    [Fact]
    public void Evaluate_RespectsBetThreshold()
    {
        var samples = new List<BacktestSample>
        {
            new(0.4, true, DecimalOdds: 2.0),
            new(0.45, false, DecimalOdds: 2.0)
        };

        var metrics = BacktestEvaluator.Evaluate(samples, betThreshold: 0.5);

        Assert.Equal(0, metrics.BetCount);
        Assert.Equal(0.0, metrics.Roi, 6);
    }

    [Fact]
    public void Evaluate_StakeableSubset_IgnoresNoEdgeBets()
    {
        var samples = new List<BacktestSample>
        {
            new(0.70, true, DecimalOdds: 2.0, FairMarketProbability: 0.52),
            new(0.56, false, DecimalOdds: 1.80, FairMarketProbability: 0.55)
        };

        var stakeable = samples
            .Where(sample =>
                sample.FairMarketProbability is double market &&
                BetPricingMath.MeetsMinimumEdge(sample.Probability, market, 0.03))
            .ToList();
        var metrics = BacktestEvaluator.Evaluate(stakeable, betThreshold: 0.55);

        Assert.Equal(1, metrics.BetCount);
        Assert.Equal(1.0, metrics.Roi, 6);
    }

    [Fact]
    public void Evaluate_EmptyInput_ReturnsEmptyMetrics()
    {
        var metrics = BacktestEvaluator.Evaluate([]);

        Assert.Equal(0, metrics.SampleCount);
    }
}
