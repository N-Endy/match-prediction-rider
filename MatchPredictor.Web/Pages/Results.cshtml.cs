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
                return new SettledResultRow(
                    prediction,
                    isWin,
                    FormatMarketLabel(prediction.PredictionCategory),
                    prediction.MatchLocalDate.ToString("dd MMM yyyy"));
            })
            .ToList();

        TotalCount = Rows.Count;
        WinCount = Rows.Count(row => row.IsWin);
        LossCount = TotalCount - WinCount;
        OverallHitRate = TotalCount == 0 ? 0 : (double)WinCount / TotalCount;

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

    public sealed record SettledResultRow(
        Prediction Prediction,
        bool IsWin,
        string MarketLabel,
        string DateLabel);

    public sealed record MarketSummaryRow(
        string MarketLabel,
        int Total,
        int Wins,
        int Losses,
        double HitRate);
}
