using MatchPredictor.Application.Helpers;
using MatchPredictor.Application.Services;
using MatchPredictor.Domain.Helpers;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace MatchPredictor.Web.Pages;

public class BetslipsModel : PageModel
{
    private readonly IBetslipQueries _betslipQueries;
    private readonly BetslipSettings _settings;

    public BetslipsModel(IBetslipQueries betslipQueries, IOptions<BetslipSettings> betslipOptions)
    {
        _betslipQueries = betslipQueries;
        _settings = betslipOptions.Value;
    }

    public BetslipSet? CurrentSet { get; private set; }
    public string GeneratedLocalLabel { get; private set; } = string.Empty;
    public Betslip? RolloverSlip { get; private set; }
    public Betslip? BankerSlip { get; private set; }
    public IReadOnlyList<Betslip> OtherSlips { get; private set; } = [];
    public decimal ReferenceStakeNaira { get; private set; } = 100m;

    public BetslipRecordSection RecordSection { get; private set; } = BetslipRecordSection.Banker;
    public DateOnly RecordDate { get; private set; }
    public DateOnly CalendarMonth { get; private set; }
    public IReadOnlyList<BetslipCalendarDay> CalendarDays { get; private set; } = [];
    public IReadOnlyList<BetslipRecordRunViewModel> RecordRuns { get; private set; } = [];
    public bool HasRecordDateSelection { get; private set; }

    public string RecordSectionSlug => BetslipKinds.ToSlug(RecordSection);

    public async Task OnGetAsync(string? record, DateOnly? date, string? month, CancellationToken ct)
    {
        ReferenceStakeNaira = _settings.ReferenceStakeNaira > 0 ? _settings.ReferenceStakeNaira : 100m;
        ViewData["ReferenceStakeNaira"] = ReferenceStakeNaira;

        CurrentSet = await _betslipQueries.GetCurrentSetAsync(ct);
        if (CurrentSet is not null)
        {
            var local = DateTimeProvider.ConvertUtcToLocal(CurrentSet.GeneratedAtUtc);
            GeneratedLocalLabel = $"{local:ddd d MMM yyyy, HH:mm} WAT";

            RolloverSlip = CurrentSet.Slips
                .FirstOrDefault(s => BetslipGenerationService.IsRolloverSlip(s));

            BankerSlip = CurrentSet.Slips
                .FirstOrDefault(s => BetslipGenerationService.IsBankerSlip(s));

            OtherSlips = CurrentSet.Slips
                .Where(s => s != RolloverSlip && s != BankerSlip)
                .OrderBy(s => s.SlipNumber)
                .ToList();
        }

        RecordSection = BetslipKinds.ParseSectionOrDefault(record);
        var today = DateTimeProvider.GetLocalDate();
        var latest = await _betslipQueries.GetLatestSlipDateAsync(RecordSection, ct);

        var monthStart = ParseMonth(month);
        if (date is not null)
        {
            RecordDate = date.Value;
            HasRecordDateSelection = true;
            CalendarMonth = monthStart ?? new DateOnly(RecordDate.Year, RecordDate.Month, 1);
        }
        else if (monthStart is not null)
        {
            CalendarMonth = monthStart.Value;
            RecordDate = latest is { } latestDate &&
                         latestDate.Year == CalendarMonth.Year &&
                         latestDate.Month == CalendarMonth.Month
                ? latestDate
                : CalendarMonth;
            HasRecordDateSelection = latest is not null &&
                                     latest.Value.Year == CalendarMonth.Year &&
                                     latest.Value.Month == CalendarMonth.Month;
        }
        else if (latest is not null)
        {
            RecordDate = latest.Value;
            HasRecordDateSelection = true;
            CalendarMonth = new DateOnly(RecordDate.Year, RecordDate.Month, 1);
        }
        else
        {
            RecordDate = today;
            HasRecordDateSelection = false;
            CalendarMonth = new DateOnly(today.Year, today.Month, 1);
        }

        var datesWithSlips = await _betslipQueries.GetSlipDatesAsync(
            RecordSection,
            CalendarMonth.Year,
            CalendarMonth.Month,
            ct);
        var datesSet = datesWithSlips.ToHashSet();

        if (date is null &&
            HasRecordDateSelection &&
            !datesSet.Contains(RecordDate) &&
            RecordDate.Year == CalendarMonth.Year &&
            RecordDate.Month == CalendarMonth.Month)
        {
            HasRecordDateSelection = false;
        }

        CalendarDays = BuildCalendar(CalendarMonth, datesSet, RecordDate, today, HasRecordDateSelection);

        if (!HasRecordDateSelection)
        {
            return;
        }

        var records = await _betslipQueries.GetSlipsForDateAsync(RecordSection, RecordDate, ct);
        var utcNow = DateTime.UtcNow;
        RecordRuns = records.Runs
            .Select(run => new BetslipRecordRunViewModel
            {
                RunLabel = run.RunLabel,
                GeneratedAtUtc = run.GeneratedAtUtc,
                DayKind = run.DayKind,
                Slips = run.Slips
                    .Select(slip => new BetslipRecordCardViewModel
                    {
                        Slip = slip,
                        HitStatusBySelectionId = slip.Selections.ToDictionary(
                            selection => selection.Id,
                            selection =>
                            {
                                Prediction? prediction = null;
                                if (selection.PredictionId is int predictionId)
                                {
                                    records.PredictionsById.TryGetValue(predictionId, out prediction);
                                }

                                return BetslipSelectionHitMapper.Map(prediction, utcNow);
                            })
                    })
                    .ToList()
            })
            .ToList();
    }

    public string RecordUrl(BetslipRecordSection section, DateOnly? date = null, DateOnly? month = null)
    {
        var slug = BetslipKinds.ToSlug(section);
        if (date is not null)
        {
            return $"/betslips?record={slug}&date={date.Value:yyyy-MM-dd}";
        }

        if (month is not null)
        {
            return $"/betslips?record={slug}&month={month.Value:yyyy-MM}";
        }

        return $"/betslips?record={slug}";
    }

    public static string FormatRunLabel(string runLabel) =>
        runLabel.ToLowerInvariant() switch
        {
            BetslipRunLabels.Morning => "Morning",
            BetslipRunLabels.Midday => "Midday",
            _ => string.IsNullOrWhiteSpace(runLabel) ? "Run" : runLabel
        };

    private static DateOnly? ParseMonth(string? month)
    {
        if (string.IsNullOrWhiteSpace(month))
        {
            return null;
        }

        if (DateOnly.TryParse($"{month.Trim()}-01", out var parsed))
        {
            return new DateOnly(parsed.Year, parsed.Month, 1);
        }

        return null;
    }

    private static IReadOnlyList<BetslipCalendarDay> BuildCalendar(
        DateOnly monthStart,
        IReadOnlySet<DateOnly> datesWithSlips,
        DateOnly selectedDate,
        DateOnly today,
        bool hasSelection)
    {
        var startOffset = ((int)monthStart.DayOfWeek + 6) % 7;
        var gridStart = monthStart.AddDays(-startOffset);
        var days = new List<BetslipCalendarDay>(42);
        for (var i = 0; i < 42; i++)
        {
            var day = gridStart.AddDays(i);
            days.Add(new BetslipCalendarDay
            {
                Date = day,
                IsInMonth = day.Month == monthStart.Month && day.Year == monthStart.Year,
                HasSlips = datesWithSlips.Contains(day),
                IsSelected = hasSelection && day == selectedDate,
                IsToday = day == today
            });
        }

        return days;
    }
}

public sealed class BetslipCalendarDay
{
    public DateOnly Date { get; init; }
    public bool IsInMonth { get; init; }
    public bool HasSlips { get; init; }
    public bool IsSelected { get; init; }
    public bool IsToday { get; init; }
}

public sealed class BetslipRecordRunViewModel
{
    public string RunLabel { get; init; } = string.Empty;
    public DateTime GeneratedAtUtc { get; init; }
    public string DayKind { get; init; } = string.Empty;
    public IReadOnlyList<BetslipRecordCardViewModel> Slips { get; init; } = [];
}

public sealed class BetslipRecordCardViewModel
{
    public required Betslip Slip { get; init; }
    public IReadOnlyDictionary<int, BetslipSelectionHitStatus> HitStatusBySelectionId { get; init; } =
        new Dictionary<int, BetslipSelectionHitStatus>();
}
