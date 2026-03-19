using System.Globalization;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MatchPredictor.Web.Pages;

public class AnalyticsModel : PageModel
{
    private readonly ApplicationDbContext _db;
    private readonly IForecastEvaluationService _forecastEvaluationService;
    private readonly PredictionSettings _settings;

    public string DefaultTabId { get; private set; } = "7days";
    public AnalyticsStats TodayStats { get; set; } = new();
    public AnalyticsStats YesterdayStats { get; set; } = new();
    public AnalyticsStats Last3DaysStats { get; set; } = new();
    public AnalyticsStats Last7DaysStats { get; set; } = new();
    public AnalyticsLiveConfigSnapshot CurrentLiveConfig { get; set; } = new();

    public AnalyticsModel(
        ApplicationDbContext db,
        IForecastEvaluationService forecastEvaluationService,
        IOptions<PredictionSettings> options)
    {
        _db = db;
        _forecastEvaluationService = forecastEvaluationService;
        _settings = options.Value;
    }

    public async Task OnGetAsync()
    {
        await LoadAnalyticsDataAsync();
    }

    public static string BuildCurvePoints(IEnumerable<ReliabilityCurvePoint> points)
    {
        return string.Join(
            " ",
            points.Select(point =>
                $"{CurveX(point).ToString("F2", CultureInfo.InvariantCulture)},{CurveY(point).ToString("F2", CultureInfo.InvariantCulture)}"));
    }

    public static double CurveX(ReliabilityCurvePoint point) => point.AveragePredictedProbability * 100.0;

    public static double CurveY(ReliabilityCurvePoint point) => 100.0 - (point.ObservedFrequency * 100.0);

    private async Task LoadAnalyticsDataAsync()
    {
        var today = DateOnly.FromDateTime(DateTimeProvider.GetLocalTime());
        var dateSetToday = new HashSet<DateOnly>([today]);
        var dateSetYesterday = new HashSet<DateOnly>([today.AddDays(-1)]);
        var dateSetLast3 = Enumerable.Range(0, 3).Select(i => today.AddDays(-i)).ToHashSet();
        var dateSetLast7 = Enumerable.Range(0, 7).Select(i => today.AddDays(-i)).ToHashSet();

        var last7Predictions = await _db.Predictions
            .Where(prediction => dateSetLast7.Contains(prediction.MatchLocalDate))
            .ToListAsync();

        var last7Forecasts = await _db.ForecastObservations
            .Where(forecast => dateSetLast7.Contains(forecast.MatchLocalDate))
            .ToListAsync();
        var thresholdProfiles = await _db.ThresholdProfiles
            .AsNoTracking()
            .ToDictionaryAsync(profile => profile.Market);
        var betaProfiles = await _db.BetaCalibrationProfiles
            .AsNoTracking()
            .ToDictionaryAsync(profile => profile.Market);
        var recentPromotionHistory = await _db.PromotionHistories
            .AsNoTracking()
            .Where(history => history.EffectiveAt >= DateTime.UtcNow.AddDays(-30))
            .OrderByDescending(history => history.EffectiveAt)
            .ToListAsync();

        TodayStats = _forecastEvaluationService.CalculateStats(
            last7Predictions.Where(prediction => dateSetToday.Contains(prediction.MatchLocalDate)),
            last7Forecasts.Where(forecast => dateSetToday.Contains(forecast.MatchLocalDate)));
        EnrichForecastStats(TodayStats, thresholdProfiles, betaProfiles);
        TodayStats.PromotionTimeline = BuildPromotionTimeline(recentPromotionHistory, dateSetToday);

        YesterdayStats = _forecastEvaluationService.CalculateStats(
            last7Predictions.Where(prediction => dateSetYesterday.Contains(prediction.MatchLocalDate)),
            last7Forecasts.Where(forecast => dateSetYesterday.Contains(forecast.MatchLocalDate)));
        EnrichForecastStats(YesterdayStats, thresholdProfiles, betaProfiles);
        YesterdayStats.PromotionTimeline = BuildPromotionTimeline(recentPromotionHistory, dateSetYesterday);

        Last3DaysStats = _forecastEvaluationService.CalculateStats(
            last7Predictions.Where(prediction => dateSetLast3.Contains(prediction.MatchLocalDate)),
            last7Forecasts.Where(forecast => dateSetLast3.Contains(forecast.MatchLocalDate)));
        EnrichForecastStats(Last3DaysStats, thresholdProfiles, betaProfiles);
        Last3DaysStats.PromotionTimeline = BuildPromotionTimeline(recentPromotionHistory, dateSetLast3);

        Last7DaysStats = _forecastEvaluationService.CalculateStats(last7Predictions, last7Forecasts);
        EnrichForecastStats(Last7DaysStats, thresholdProfiles, betaProfiles);
        Last7DaysStats.PromotionTimeline = BuildPromotionTimeline(recentPromotionHistory, dateSetLast7);

        CurrentLiveConfig = BuildLiveConfigSnapshot(
            thresholdProfiles,
            betaProfiles,
            recentPromotionHistory,
            DateTimeProvider.GetLocalTime());

        DefaultTabId = SelectDefaultTab();
    }

    private void EnrichForecastStats(
        AnalyticsStats stats,
        IReadOnlyDictionary<PredictionMarket, ThresholdProfile> thresholdProfiles,
        IReadOnlyDictionary<PredictionMarket, BetaCalibrationProfile> betaProfiles)
    {
        foreach (var marketStat in stats.ForecastMarketStats)
        {
            var fallbackThreshold = GetFallbackThreshold(marketStat.Market);
            marketStat.FallbackThreshold = fallbackThreshold;
            marketStat.ActiveThreshold = fallbackThreshold;
            marketStat.ThresholdSource = "Configured";
            marketStat.ActiveCalibrator = "Bucket";

            if (thresholdProfiles.TryGetValue(marketStat.Market, out var thresholdProfile))
            {
                marketStat.ActiveThreshold = thresholdProfile.IsPromoted ? thresholdProfile.Threshold : fallbackThreshold;
                marketStat.ThresholdSource = thresholdProfile.IsPromoted ? "Tuned" : "Configured";
                marketStat.ThresholdSampleCount = thresholdProfile.SampleCount;
                marketStat.ThresholdHitRate = thresholdProfile.HitRate;
                marketStat.ThresholdPublishedPerWeek = thresholdProfile.PublishedPerWeek;
                marketStat.ThresholdBrierScore = thresholdProfile.BrierScore;
                marketStat.ThresholdLastUpdated = thresholdProfile.LastUpdated;
            }

            if (betaProfiles.TryGetValue(marketStat.Market, out var betaProfile))
            {
                marketStat.BetaBaselineBrierScore = betaProfile.BaselineBrierScore;
                marketStat.BetaValidationBrierScore = betaProfile.ValidationBrierScore;
                marketStat.BetaImprovement = betaProfile.Improvement;
                marketStat.BetaRecommended = betaProfile.IsRecommended;
                marketStat.BetaTrainingSampleCount = betaProfile.TrainingSampleCount;
                marketStat.BetaValidationSampleCount = betaProfile.ValidationSampleCount;
                marketStat.BetaLastUpdated = betaProfile.LastUpdated;
                if (betaProfile.IsRecommended)
                {
                    marketStat.ActiveCalibrator = "Beta";
                }
            }
        }
    }

    private string SelectDefaultTab()
    {
        if (TodayStats.SettledForecasts > 0 || TodayStats.ForecastMarketStats.Any())
            return "today";

        if (YesterdayStats.SettledForecasts > 0 || YesterdayStats.ForecastMarketStats.Any())
            return "yesterday";

        if (Last3DaysStats.SettledForecasts > 0 || Last3DaysStats.ForecastMarketStats.Any())
            return "3days";

        return "7days";
    }

    private AnalyticsLiveConfigSnapshot BuildLiveConfigSnapshot(
        IReadOnlyDictionary<PredictionMarket, ThresholdProfile> thresholdProfiles,
        IReadOnlyDictionary<PredictionMarket, BetaCalibrationProfile> betaProfiles,
        IReadOnlyList<PromotionHistory> recentPromotionHistory,
        DateTime generatedAtLocal)
    {
        var markets = Enum.GetValues<PredictionMarket>()
            .Where(market => market != PredictionMarket.Draw)
            .OrderBy(market => market)
            .Select(market =>
            {
                var fallbackThreshold = GetFallbackThreshold(market);
                thresholdProfiles.TryGetValue(market, out var thresholdProfile);
                betaProfiles.TryGetValue(market, out var betaProfile);

                return new LiveMarketConfigStat
                {
                    Market = market,
                    MarketName = market.ToDisplayName(),
                    FallbackThreshold = fallbackThreshold,
                    ActiveThreshold = thresholdProfile?.IsPromoted == true ? thresholdProfile.Threshold : fallbackThreshold,
                    ThresholdSource = thresholdProfile?.IsPromoted == true ? "Tuned" : "Configured",
                    ThresholdSampleCount = thresholdProfile?.SampleCount ?? 0,
                    ThresholdHitRate = thresholdProfile?.HitRate ?? 0.0,
                    ThresholdPublishedPerWeek = thresholdProfile?.PublishedPerWeek ?? 0.0,
                    ThresholdBrierScore = thresholdProfile?.BrierScore ?? 0.0,
                    ThresholdLastUpdated = thresholdProfile?.LastUpdated,
                    ActiveCalibrator = betaProfile?.IsRecommended == true ? "Beta" : "Bucket",
                    BetaBaselineBrierScore = betaProfile?.BaselineBrierScore,
                    BetaValidationBrierScore = betaProfile?.ValidationBrierScore,
                    BetaImprovement = betaProfile?.Improvement,
                    BetaRecommended = betaProfile?.IsRecommended ?? false,
                    BetaTrainingSampleCount = betaProfile?.TrainingSampleCount,
                    BetaValidationSampleCount = betaProfile?.ValidationSampleCount,
                    BetaLastUpdated = betaProfile?.LastUpdated
                };
            })
            .ToList();

        return new AnalyticsLiveConfigSnapshot
        {
            GeneratedAtLocal = generatedAtLocal,
            Markets = markets,
            PromotionTimeline = BuildPromotionTimeline(
                recentPromotionHistory,
                recentPromotionHistory
                    .Select(history => DateOnly.FromDateTime(DateTimeProvider.ConvertUtcToLocal(history.EffectiveAt)))
                    .ToHashSet())
        };
    }

    private static List<PromotionTimelineItem> BuildPromotionTimeline(
        IEnumerable<PromotionHistory> promotionHistory,
        IReadOnlySet<DateOnly> localDates)
    {
        return promotionHistory
            .Where(history =>
                history.Market != PredictionMarket.Draw &&
                localDates.Contains(DateOnly.FromDateTime(DateTimeProvider.ConvertUtcToLocal(history.EffectiveAt))))
            .OrderByDescending(history => history.EffectiveAt)
            .Select(history =>
            {
                var localTime = DateTimeProvider.ConvertUtcToLocal(history.EffectiveAt);
                return new PromotionTimelineItem
                {
                    EffectiveAt = localTime,
                    MarketName = history.Market.ToDisplayName(),
                    ChangeType = history.ChangeType,
                    Summary = BuildTimelineSummary(history),
                    Detail = BuildTimelineDetail(history),
                    Improvement = history.Improvement
                };
            })
            .ToList();
    }

    private static string BuildTimelineSummary(PromotionHistory history)
    {
        if (history.ChangeType == "Threshold")
        {
            return $"{history.PreviousValue} {FormatNumeric(history.PreviousNumericValue)} -> {history.NewValue} {FormatNumeric(history.NewNumericValue)}";
        }

        return $"{history.PreviousValue} -> {history.NewValue}";
    }

    private static string BuildTimelineDetail(PromotionHistory history)
    {
        if (history.Improvement.HasValue)
        {
            return $"Delta {history.Improvement.Value:+0.000;-0.000;0.000}";
        }

        if (history.BaselineScore.HasValue && history.CandidateScore.HasValue)
        {
            return $"Base {history.BaselineScore.Value:F3}, New {history.CandidateScore.Value:F3}";
        }

        return "State change recorded";
    }

    private static string FormatNumeric(double? value)
    {
        return value.HasValue ? value.Value.ToString("F2") : "--";
    }

    private double GetFallbackThreshold(PredictionMarket market)
    {
        return market switch
        {
            PredictionMarket.BothTeamsScore => _settings.BttsScoreThreshold,
            PredictionMarket.Over25Goals => _settings.OverTwoGoalsStrongThreshold,
            PredictionMarket.Under25Goals => _settings.UnderTwoGoalsStrongThreshold,
            PredictionMarket.Draw => _settings.DrawStrongThreshold,
            PredictionMarket.HomeWin => _settings.HomeWinStrong,
            PredictionMarket.AwayWin => _settings.AwayWinStrong,
            _ => 0.0
        };
    }
}
