using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MatchPredictor.Web.Services;

public sealed class AnalyticsDataSnapshot
{
    public IReadOnlyList<Prediction> Predictions { get; init; } = [];
    public IReadOnlyList<ForecastObservation> Forecasts { get; init; } = [];
    public IReadOnlyList<PredictionOddsSnapshot> OddsSnapshots { get; init; } = [];
    public IReadOnlyDictionary<PredictionMarket, ThresholdProfile> ThresholdProfiles { get; init; }
        = new Dictionary<PredictionMarket, ThresholdProfile>();
    public IReadOnlyDictionary<PredictionMarket, BetaCalibrationProfile> BetaProfiles { get; init; }
        = new Dictionary<PredictionMarket, BetaCalibrationProfile>();
    public IReadOnlyDictionary<PredictionMarket, IsotonicCalibrationProfile> IsotonicProfiles { get; init; }
        = new Dictionary<PredictionMarket, IsotonicCalibrationProfile>();
    public IReadOnlyList<HistoricalBacktestSummary> BacktestTrend { get; init; } = [];
    public IReadOnlyList<MarketMlModelProfile> MarketMlProfiles { get; init; } = [];
    public IReadOnlyList<PromotionHistory> RecentPromotionHistory { get; init; } = [];
}

public interface IAnalyticsQueries
{
    Task<AnalyticsDataSnapshot> GetSnapshotAsync(
        IReadOnlyCollection<DateOnly> windowDates,
        DateTime promotionCutoffUtc,
        CancellationToken cancellationToken = default);
}

public sealed class AnalyticsQueries : IAnalyticsQueries
{
    private readonly ApplicationDbContext _dbContext;

    public AnalyticsQueries(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<AnalyticsDataSnapshot> GetSnapshotAsync(
        IReadOnlyCollection<DateOnly> windowDates,
        DateTime promotionCutoffUtc,
        CancellationToken cancellationToken = default)
    {
        var windowDateSet = windowDates.ToHashSet();

        var predictions = await _dbContext.Predictions
            .AsNoTracking()
            .Where(prediction => windowDateSet.Contains(prediction.MatchLocalDate) && prediction.WasPublished)
            .ToListAsync(cancellationToken);

        var forecasts = await _dbContext.ForecastObservations
            .AsNoTracking()
            .Where(forecast => windowDateSet.Contains(forecast.MatchLocalDate))
            .ToListAsync(cancellationToken);

        var predictionIds = predictions.Select(prediction => prediction.Id).ToHashSet();
        var oddsSnapshots = await _dbContext.PredictionOddsSnapshots
            .AsNoTracking()
            .Where(snapshot => predictionIds.Contains(snapshot.PredictionId))
            .Where(snapshot =>
                snapshot.SnapshotKind == PredictionOddsSnapshotKind.Publish ||
                snapshot.SnapshotKind == PredictionOddsSnapshotKind.Close)
            .ToListAsync(cancellationToken);

        var thresholdProfiles = await _dbContext.ThresholdProfiles
            .AsNoTracking()
            .ToDictionaryAsync(profile => profile.Market, cancellationToken);

        var betaProfiles = await _dbContext.BetaCalibrationProfiles
            .AsNoTracking()
            .ToDictionaryAsync(profile => profile.Market, cancellationToken);

        var isotonicProfiles = await _dbContext.IsotonicCalibrationProfiles
            .AsNoTracking()
            .ToDictionaryAsync(profile => profile.Market, cancellationToken);

        var backtestTrend = await _dbContext.HistoricalBacktestSummaries
            .AsNoTracking()
            .OrderByDescending(summary => summary.RunAtUtc)
            .Take(30)
            .ToListAsync(cancellationToken);

        var marketMlProfiles = await _dbContext.MarketMlModelProfiles
            .AsNoTracking()
            .OrderBy(profile => profile.Market)
            .ToListAsync(cancellationToken);

        var recentPromotionHistory = await _dbContext.PromotionHistories
            .AsNoTracking()
            .Where(history => history.EffectiveAt >= promotionCutoffUtc)
            .OrderByDescending(history => history.EffectiveAt)
            .ToListAsync(cancellationToken);

        return new AnalyticsDataSnapshot
        {
            Predictions = predictions,
            Forecasts = forecasts,
            OddsSnapshots = oddsSnapshots,
            ThresholdProfiles = thresholdProfiles,
            BetaProfiles = betaProfiles,
            IsotonicProfiles = isotonicProfiles,
            BacktestTrend = backtestTrend,
            MarketMlProfiles = marketMlProfiles,
            RecentPromotionHistory = recentPromotionHistory
        };
    }
}
