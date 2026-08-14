using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Statistics.Backtesting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MatchPredictor.Infrastructure.Services;

public sealed class HistoricalBacktestService : IHistoricalBacktestService
{
    private const int LookbackDays = 90;

    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<HistoricalBacktestService> _logger;
    private readonly double _minimumEdge;

    public HistoricalBacktestService(
        ApplicationDbContext dbContext,
        ILogger<HistoricalBacktestService> logger,
        IOptions<PredictionSettings>? predictionOptions = null)
    {
        _dbContext = dbContext;
        _logger = logger;
        _minimumEdge = predictionOptions?.Value.ValueBetMinimumEdge ?? 0.03;
    }

    public async Task RunNightlyBacktestAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-LookbackDays);
        var forecasts = await _dbContext.ForecastObservations
            .AsNoTracking()
            .Where(forecast =>
                forecast.IsSettled &&
                forecast.OutcomeOccurred != null &&
                forecast.IsPublished &&
                (forecast.SettledAt ?? forecast.CreatedAt) >= cutoff)
            .ToListAsync(cancellationToken);

        var pointInTime = PointInTimeBacktestingSelector.SelectForecasts(forecasts);
        if (pointInTime.Count < 30)
        {
            _logger.LogInformation(
                "Skipping historical backtest — only {Count} settled published forecasts in the lookback window.",
                pointInTime.Count);
            return;
        }

        var fixtureKeys = pointInTime
            .Select(forecast => forecast.FixtureKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var marketSnapshots = fixtureKeys.Count == 0
            ? []
            : await _dbContext.MarketOddsSnapshots
                .AsNoTracking()
                .Where(snapshot => fixtureKeys.Contains(snapshot.FixtureKey))
                .ToListAsync(cancellationToken);
        var marketSnapshotsByFixture = marketSnapshots
            .GroupBy(snapshot => snapshot.FixtureKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderBy(snapshot => snapshot.CapturedAtUtc).ToList(), StringComparer.OrdinalIgnoreCase);

        var samples = pointInTime
            .Select(forecast =>
            {
                marketSnapshotsByFixture.TryGetValue(forecast.FixtureKey, out var snapshots);
                var orderedSnapshots = snapshots ?? [];
                var publishOdds = orderedSnapshots
                    .Select(snapshot => ResolveDecimalOdds(snapshot, forecast.Market))
                    .FirstOrDefault(odds => odds is > 1.0);
                var closeOdds = orderedSnapshots
                    .Select(snapshot => ResolveDecimalOdds(snapshot, forecast.Market))
                    .LastOrDefault(odds => odds is > 1.0);

                return new BacktestSample(
                    Probability: forecast.CalibratedProbability > 0 ? forecast.CalibratedProbability : forecast.RawProbability,
                    Outcome: forecast.OutcomeOccurred == true,
                    DecimalOdds: publishOdds,
                    CloseDecimalOdds: closeOdds,
                    DateUtc: forecast.MatchDateTime ?? forecast.SettledAt ?? forecast.CreatedAt,
                    FairMarketProbability: ResolveFairMarketProbability(orderedSnapshots, forecast.Market, publishOdds));
            })
            .ToList();

        var metrics = BacktestEvaluator.Evaluate(samples, betThreshold: 0.55);
        var stakeableSamples = samples
            .Where(sample =>
                sample.FairMarketProbability is double marketProbability &&
                BetPricingMath.MeetsMinimumEdge(sample.Probability, marketProbability, _minimumEdge))
            .ToList();
        var stakeableMetrics = stakeableSamples.Count == 0
            ? BacktestMetrics.Empty
            : BacktestEvaluator.Evaluate(stakeableSamples, betThreshold: 0.55);
        var summary = new HistoricalBacktestSummary
        {
            RunAtUtc = DateTime.UtcNow,
            SampleCount = metrics.SampleCount,
            BrierScore = metrics.Brier,
            ExpectedCalibrationError = metrics.Ece,
            LogLoss = metrics.LogLoss,
            FlatStakeRoiPercent = metrics.Roi * 100.0,
            AverageClvPercent = metrics.Clv * 100.0,
            StakeableBetCount = stakeableMetrics.BetCount,
            StakeableFlatStakeRoiPercent = stakeableMetrics.Roi * 100.0,
            StakeableAverageClvPercent = stakeableMetrics.Clv * 100.0,
            LookbackDays = LookbackDays
        };

        _dbContext.HistoricalBacktestSummaries.Add(summary);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Historical backtest complete: {Samples} samples, Brier={Brier:F4}, ECE={Ece:F4}, ROI={Roi:F1}%, stakeable ROI={StakeableRoi:F1}% ({StakeableBets} bets).",
            summary.SampleCount,
            summary.BrierScore,
            summary.ExpectedCalibrationError,
            summary.FlatStakeRoiPercent,
            summary.StakeableFlatStakeRoiPercent,
            summary.StakeableBetCount);
    }

    private static double? ResolveFairMarketProbability(
        IReadOnlyList<MarketOddsSnapshot> snapshots,
        PredictionMarket market,
        double? publishOdds)
    {
        var fair = snapshots
            .Select(snapshot => market switch
            {
                PredictionMarket.HomeWin => snapshot.FairHomeWin,
                PredictionMarket.AwayWin => snapshot.FairAwayWin,
                PredictionMarket.Draw => snapshot.FairDraw,
                PredictionMarket.Over25Goals => snapshot.FairOver25,
                PredictionMarket.Under25Goals => snapshot.FairUnder25,
                PredictionMarket.BothTeamsScore => snapshot.FairBttsYes,
                _ => null
            })
            .FirstOrDefault(probability => probability is > 0 and < 1);

        if (fair is > 0)
        {
            return fair;
        }

        return publishOdds is > 1.0 ? 1.0 / publishOdds.Value : null;
    }

    private static double? ResolveDecimalOdds(MarketOddsSnapshot snapshot, PredictionMarket market)
    {
        return market switch
        {
            PredictionMarket.HomeWin => snapshot.HomeWinOdds,
            PredictionMarket.AwayWin => snapshot.AwayWinOdds,
            PredictionMarket.Draw => snapshot.DrawOdds,
            PredictionMarket.Over25Goals => snapshot.Over25Odds,
            PredictionMarket.Under25Goals => snapshot.Under25Odds,
            PredictionMarket.BothTeamsScore => snapshot.BttsYesOdds,
            _ => null
        };
    }
}
