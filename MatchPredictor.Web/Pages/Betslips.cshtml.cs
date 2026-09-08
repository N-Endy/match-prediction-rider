using MatchPredictor.Application.Helpers;
using MatchPredictor.Application.Services;
using MatchPredictor.Domain.Helpers;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.AspNetCore.Mvc;
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
    public BetslipCalendarViewModel Calendar { get; private set; } = new();
    public BetslipRecordResultsViewModel Results { get; private set; } = new();
    public bool HasRecordDateSelection { get; private set; }

    public string RecordSectionSlug => BetslipKinds.ToSlug(RecordSection);

    public async Task OnGetAsync(string? record, DateOnly? date, string? month, CancellationToken ct)
    {
        ApplyStake();
        await LoadCurrentSetAsync(ct);
        await LoadRecordsAsync(record, date, month, loadResults: true, autoSelectLatest: date is null, ct);
    }

    public async Task<IActionResult> OnGetRecordDayAsync(string? record, DateOnly date, CancellationToken ct)
    {
        ApplyStake();
        await LoadRecordsAsync(record, date, month: null, loadResults: true, autoSelectLatest: false, ct);
        return Partial("_BetslipRecordResults", Results);
    }

    public async Task<IActionResult> OnGetRecordCalendarAsync(
        string? record,
        string? month,
        DateOnly? date,
        CancellationToken ct)
    {
        ApplyStake();
        await LoadRecordsAsync(record, date, month, loadResults: false, autoSelectLatest: false, ct);
        return Partial("_BetslipCalendar", Calendar);
    }

    public async Task<IActionResult> OnGetLatestRecordDateAsync(string? record, CancellationToken ct)
    {
        var section = BetslipKinds.ParseSectionOrDefault(record);
        var latest = await _betslipQueries.GetLatestSlipDateAsync(section, ct);
        return new JsonResult(new { date = latest?.ToString("yyyy-MM-dd") });
    }

    public static string RecordUrl(BetslipRecordSection section, DateOnly? date = null, DateOnly? month = null)
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

    private void ApplyStake()
    {
        ReferenceStakeNaira = _settings.ReferenceStakeNaira > 0 ? _settings.ReferenceStakeNaira : 100m;
        ViewData["ReferenceStakeNaira"] = ReferenceStakeNaira;
    }

    private async Task LoadCurrentSetAsync(CancellationToken ct)
    {
        CurrentSet = await _betslipQueries.GetCurrentSetAsync(ct);
        if (CurrentSet is null)
        {
            return;
        }

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

    private async Task LoadRecordsAsync(
        string? record,
        DateOnly? date,
        string? month,
        bool loadResults,
        bool autoSelectLatest,
        CancellationToken ct)
    {
        RecordSection = BetslipKinds.ParseSectionOrDefault(record);
        var today = DateTimeProvider.GetLocalDate();
        var monthStart = ParseMonth(month);

        DateOnly? effectiveDate = date;
        if (effectiveDate is null && autoSelectLatest)
        {
            effectiveDate = await _betslipQueries.GetLatestSlipDateAsync(RecordSection, ct);
        }

        HasRecordDateSelection = effectiveDate is not null;
        RecordDate = effectiveDate ?? today;
        CalendarMonth = monthStart
                        ?? (effectiveDate is not null
                            ? new DateOnly(effectiveDate.Value.Year, effectiveDate.Value.Month, 1)
                            : new DateOnly(today.Year, today.Month, 1));

        var datesWithSlips = await _betslipQueries.GetSlipDatesAsync(
            RecordSection,
            CalendarMonth.Year,
            CalendarMonth.Month,
            ct);
        var datesSet = datesWithSlips.ToHashSet();
        var selectedInMonth = HasRecordDateSelection &&
                              RecordDate.Year == CalendarMonth.Year &&
                              RecordDate.Month == CalendarMonth.Month;

        Calendar = BuildCalendar(RecordSection, CalendarMonth, datesSet, RecordDate, today, selectedInMonth);

        if (!loadResults || !HasRecordDateSelection)
        {
            Results = new BetslipRecordResultsViewModel
            {
                Section = RecordSection,
                Date = RecordDate,
                HasDateSelection = false,
                ReferenceStakeNaira = ReferenceStakeNaira
            };
            return;
        }

        var records = await _betslipQueries.GetSlipsForDateAsync(RecordSection, RecordDate, ct);
        var utcNow = DateTime.UtcNow;
        Results = new BetslipRecordResultsViewModel
        {
            Section = RecordSection,
            Date = RecordDate,
            HasDateSelection = true,
            ReferenceStakeNaira = ReferenceStakeNaira,
            Runs = MapRuns(records, utcNow)
        };
    }

    private static IReadOnlyList<BetslipRecordRunViewModel> MapRuns(BetslipRecordsForDate records, DateTime utcNow)
    {
        return records.Runs
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
                            selection => BetslipSelectionHitMapper.MapSelection(
                                selection,
                                records.Date,
                                records.PredictionsById,
                                records.FallbackPredictions,
                                utcNow))
                    })
                    .ToList()
            })
            .ToList();
    }

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

    private static BetslipCalendarViewModel BuildCalendar(
        BetslipRecordSection section,
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
            var hasSlips = datesWithSlips.Contains(day);
            days.Add(new BetslipCalendarDay
            {
                Date = day,
                IsInMonth = day.Month == monthStart.Month && day.Year == monthStart.Year,
                HasSlips = hasSlips,
                IsSelected = hasSelection && day == selectedDate,
                IsToday = day == today,
                Href = hasSlips ? RecordUrl(section, day) : null
            });
        }

        var prevMonth = monthStart.AddMonths(-1);
        var nextMonth = monthStart.AddMonths(1);
        return new BetslipCalendarViewModel
        {
            Section = section,
            CalendarMonth = monthStart,
            Days = days,
            PrevMonthHref = RecordUrl(section, month: prevMonth),
            NextMonthHref = RecordUrl(section, month: nextMonth),
            PrevMonthValue = prevMonth.ToString("yyyy-MM"),
            NextMonthValue = nextMonth.ToString("yyyy-MM")
        };
    }
}

public sealed class BetslipCalendarViewModel
{
    public BetslipRecordSection Section { get; init; }
    public DateOnly CalendarMonth { get; init; }
    public IReadOnlyList<BetslipCalendarDay> Days { get; init; } = [];
    public string PrevMonthHref { get; init; } = string.Empty;
    public string NextMonthHref { get; init; } = string.Empty;
    public string PrevMonthValue { get; init; } = string.Empty;
    public string NextMonthValue { get; init; } = string.Empty;
}

public sealed class BetslipCalendarDay
{
    public DateOnly Date { get; init; }
    public bool IsInMonth { get; init; }
    public bool HasSlips { get; init; }
    public bool IsSelected { get; init; }
    public bool IsToday { get; init; }
    public string? Href { get; init; }
}

public sealed class BetslipRecordResultsViewModel
{
    public BetslipRecordSection Section { get; init; }
    public DateOnly Date { get; init; }
    public bool HasDateSelection { get; init; }
    public decimal ReferenceStakeNaira { get; init; } = 100m;
    public IReadOnlyList<BetslipRecordRunViewModel> Runs { get; init; } = [];
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
