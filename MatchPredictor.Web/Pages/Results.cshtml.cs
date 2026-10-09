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

                var dayRows = group.OrderByDescending(r => r.Prediction.MatchLocalTime ?? TimeOnly.MinValue).ToList();

                var fixtures = group
                    .GroupBy(r => GetFixtureKey(r.Prediction))
                    .Select(fg =>
                    {
                        var first = fg.First();
                        var p = first.Prediction;
                        var picks = fg
                            .OrderBy(r => r.MarketLabel)
                            .ToList();
                        var actualScore = fg
                            .Select(r => r.Prediction.ActualScore)
                            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? p.ActualScore;
                        return new SettledFixtureGroup(
                            fg.Key,
                            p.League,
                            p.HomeTeam,
                            p.AwayTeam,
                            first.KickoffTime,
                            actualScore,
                            p.MatchLocalTime ?? DateTimeProvider.ParseLocalTimeOrNull(p.Time),
                            picks);
                    })
                    .OrderByDescending(f => f.SortTime ?? TimeOnly.MinValue)
                    .ThenBy(f => f.League)
                    .ThenBy(f => f.HomeTeam)
                    .ToList();

                return new ResultDayGroup(
                    date,
                    heading,
                    relativeLabel,
                    total,
                    wins,
                    losses,
                    hitRate,
                    dayRows,
                    fixtures);
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

    private static string GetFixtureKey(Prediction p)
    {
        if (!string.IsNullOrWhiteSpace(p.FixtureKey))
        {
            return p.FixtureKey;
        }
        return $"{p.MatchLocalDate:yyyy-MM-dd}|{(p.League ?? string.Empty).Trim().ToLowerInvariant()}|{(p.HomeTeam ?? string.Empty).Trim().ToLowerInvariant()}|{(p.AwayTeam ?? string.Empty).Trim().ToLowerInvariant()}";
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
        IReadOnlyList<SettledResultRow> Rows,
        IReadOnlyList<SettledFixtureGroup> Fixtures);

    public sealed record SettledFixtureGroup(
        string FixtureKey,
        string League,
        string HomeTeam,
        string AwayTeam,
        string KickoffTime,
        string? ActualScore,
        TimeOnly? SortTime,
        IReadOnlyList<SettledResultRow> Picks);

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
