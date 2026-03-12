using System.Globalization;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatchPredictor.Web.Pages;

public class AnalyticsModel : PageModel
{
    private readonly ApplicationDbContext _db;
    private readonly IForecastEvaluationService _forecastEvaluationService;

    public string DefaultTabId { get; private set; } = "7days";
    public AnalyticsStats TodayStats { get; set; } = new();
    public AnalyticsStats YesterdayStats { get; set; } = new();
    public AnalyticsStats Last3DaysStats { get; set; } = new();
    public AnalyticsStats Last7DaysStats { get; set; } = new();

    public AnalyticsModel(ApplicationDbContext db, IForecastEvaluationService forecastEvaluationService)
    {
        _db = db;
        _forecastEvaluationService = forecastEvaluationService;
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
        var today = DateTimeProvider.GetLocalTime().Date;

        var dateStringsToday = new[] { today.ToString("dd-MM-yyyy") };
        var dateStringsYesterday = new[] { today.AddDays(-1).ToString("dd-MM-yyyy") };
        var dateStringsLast3 = Enumerable.Range(0, 3).Select(i => today.AddDays(-i).ToString("dd-MM-yyyy")).ToArray();
        var dateStringsLast7 = Enumerable.Range(0, 7).Select(i => today.AddDays(-i).ToString("dd-MM-yyyy")).ToArray();

        var last7Predictions = await _db.Predictions
            .Where(prediction => dateStringsLast7.Contains(prediction.Date))
            .ToListAsync();

        var last7Forecasts = await _db.ForecastObservations
            .Where(forecast => dateStringsLast7.Contains(forecast.Date))
            .ToListAsync();

        TodayStats = _forecastEvaluationService.CalculateStats(
            last7Predictions.Where(prediction => dateStringsToday.Contains(prediction.Date)),
            last7Forecasts.Where(forecast => dateStringsToday.Contains(forecast.Date)));

        YesterdayStats = _forecastEvaluationService.CalculateStats(
            last7Predictions.Where(prediction => dateStringsYesterday.Contains(prediction.Date)),
            last7Forecasts.Where(forecast => dateStringsYesterday.Contains(forecast.Date)));

        Last3DaysStats = _forecastEvaluationService.CalculateStats(
            last7Predictions.Where(prediction => dateStringsLast3.Contains(prediction.Date)),
            last7Forecasts.Where(forecast => dateStringsLast3.Contains(forecast.Date)));

        Last7DaysStats = _forecastEvaluationService.CalculateStats(last7Predictions, last7Forecasts);

        DefaultTabId = SelectDefaultTab();
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
}
