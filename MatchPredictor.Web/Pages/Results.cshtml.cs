using MatchPredictor.Application.Helpers;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatchPredictor.Web.Pages;

public class ResultsModel : PageModel
{
    public const int DefaultWindowDays = 30;

    private readonly IPredictionQueries _predictionQueries;

    public ResultsModel(IPredictionQueries predictionQueries)
    {
        _predictionQueries = predictionQueries;
    }

    public IReadOnlyList<SettledResultRow> Rows { get; private set; } = [];
    public IReadOnlyList<ResultDayGroup> DayGroups { get; private set; } = [];
    public IReadOnlyList<MarketSummaryRow> MarketSummaries { get; private set; } = [];
    public int WindowDays { get; private set; } = DefaultWindowDays;
    public int TotalCount { get; private set; }
    public int WinCount { get; private set; }
    public int LossCount { get; private set; }
    public double OverallHitRate { get; private set; }
    public DateOnly? FromLocalDate { get; private set; }
    public DateOnly? ToLocalDate { get; private set; }

    public async Task OnGetAsync()
    {
        WindowDays = DefaultWindowDays;
        var settled = await _predictionQueries.GetRecentSettledPublishedAsync(WindowDays);
        var todayLocal = DateOnly.FromDateTime(DateTimeProvider.GetLocalTime());
        FromLocalDate = todayLocal.AddDays(-(WindowDays - 1));
        ToLocalDate = todayLocal;

        Rows = settled
            .Select(prediction =>
            {
                var isWin = PredictionScoreClassHelper.IsPredictionCorrect(prediction);
                var time = prediction.MatchLocalTime.HasValue
                    ? prediction.MatchLocalTime.Value.ToString("HH:mm")
                    : (!string.IsNullOrWhiteSpace(prediction.Time) ? prediction.Time : "—");
                return new SettledResultRow(
                    prediction,
                    isWin,
                    FormatMarketLabel(prediction.PredictionCategory),
                    prediction.MatchLocalDate.ToString("dd MMM yyyy"),
                    time);
            })
            .ToList();

        TotalCount = Rows.Count;
        WinCount = Rows.Count(row => row.IsWin);
        LossCount = TotalCount - WinCount;
        OverallHitRate = TotalCount == 0 ? 0 : (double)WinCount / TotalCount;

        DayGroups = Rows
            .GroupBy(row => row.Prediction.MatchLocalDate)
            .OrderByDescending(group => group.Key)
            .Select(group =>
            {
                var date = group.Key;
                var total = group.Count();
                var wins = group.Count(r => r.IsWin);
                var losses = total - wins;
                var hitRate = total == 0 ? 0 : (double)wins / total;

                string relativeLabel;
                string heading;
                if (date == todayLocal)
                {
                    relativeLabel = "Today";
                    heading = $"Today, {date:dd MMM yyyy}";
                }
                else if (date == todayLocal.AddDays(-1))
                {
                    relativeLabel = "Yesterday";
                    heading = $"Yesterday, {date:dd MMM yyyy}";
                }
                else
                {
                    relativeLabel = date.ToString("ddd");
                    heading = date.ToString("ddd, dd MMM yyyy");
                }

                return new ResultDayGroup(
                    date,
                    heading,
                    relativeLabel,
                    total,
                    wins,
                    losses,
                    hitRate,
                    group.OrderByDescending(r => r.Prediction.MatchLocalTime ?? TimeOnly.MinValue).ToList());
            })
            .ToList();

        MarketSummaries = Rows
            .GroupBy(row => row.MarketLabel)
            .Select(group =>
            {
                var count = group.Count();
                var wins = group.Count(row => row.IsWin);
                return new MarketSummaryRow(
                    group.Key,
                    count,
                    wins,
                    count - wins,
                    count == 0 ? 0 : (double)wins / count);
            })
            .OrderByDescending(summary => summary.Total)
            .ThenBy(summary => summary.MarketLabel)
            .ToList();
    }

    public static string FormatMarketLabel(string? category)
    {
        return category switch
        {
            "BothTeamsScore" => "BTTS",
            "Over2.5Goals" => "Over 2.5",
            "Under2.5Goals" => "Under 2.5",
            "StraightWin" => "Straight Win",
            "Draw" => "Draw",
            _ => string.IsNullOrWhiteSpace(category) ? "Other" : category
        };
    }

    public sealed record ResultDayGroup(
        DateOnly Date,
        string DateHeading,
        string RelativeLabel,
        int TotalCount,
        int WinCount,
        int LossCount,
        double HitRate,
        IReadOnlyList<SettledResultRow> Rows);

    public sealed record SettledResultRow(
        Prediction Prediction,
        bool IsWin,
        string MarketLabel,
        string DateLabel,
        string KickoffTime = "—");

    public sealed record MarketSummaryRow(
        string MarketLabel,
        int Total,
        int Wins,
        int Losses,
        double HitRate);
}
