using System.Linq.Expressions;
using MatchPredictor.Domain.Helpers;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.EntityFrameworkCore;

namespace MatchPredictor.Infrastructure.Repositories;

public class BetslipQueries : IBetslipQueries
{
    private readonly ApplicationDbContext _context;

    public BetslipQueries(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<BetslipSet?> GetCurrentSetAsync(CancellationToken ct = default)
    {
        return await _context.BetslipSets
            .AsNoTracking()
            .AsSplitQuery()
            .Include(s => s.Slips)
                .ThenInclude(slip => slip.Selections)
            .Where(s => s.IsCurrent)
            .OrderByDescending(s => s.GeneratedAtUtc)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<DateOnly>> GetSlipDatesAsync(
        BetslipRecordSection section,
        int year,
        int month,
        CancellationToken ct = default)
    {
        var monthStart = new DateOnly(year, month, 1);
        var monthEnd = monthStart.AddMonths(1);

        var rows = await ProjectSlipDatesAsync(
            slip => slip.BetslipSet!.SlipLocalDate >= monthStart &&
                    slip.BetslipSet.SlipLocalDate < monthEnd,
            ct);

        return rows
            .Where(row => MatchesSection(row, section))
            .Select(row => row.SlipLocalDate)
            .Distinct()
            .OrderBy(date => date)
            .ToList();
    }

    public async Task<DateOnly?> GetLatestSlipDateAsync(
        BetslipRecordSection section,
        CancellationToken ct = default)
    {
        var rows = await ProjectSlipDatesAsync(_ => true, ct);
        var matchingDates = rows
            .Where(row => MatchesSection(row, section))
            .Select(row => row.SlipLocalDate)
            .ToList();
        return matchingDates.Count == 0 ? null : matchingDates.Max();
    }

    public async Task<BetslipRecordsForDate> GetSlipsForDateAsync(
        BetslipRecordSection section,
        DateOnly date,
        CancellationToken ct = default)
    {
        var sets = await _context.BetslipSets
            .AsNoTracking()
            .AsSplitQuery()
            .Include(s => s.Slips)
                .ThenInclude(slip => slip.Selections)
            .Where(s => s.SlipLocalDate == date)
            .OrderBy(s => s.GeneratedAtUtc)
            .ToListAsync(ct);

        var runs = new List<BetslipRecordRun>();
        foreach (var set in sets)
        {
            var slips = set.Slips
                .Where(slip => BetslipKinds.MatchesSection(slip, section))
                .OrderBy(slip => slip.SlipNumber)
                .ToList();
            if (slips.Count == 0)
            {
                continue;
            }

            runs.Add(new BetslipRecordRun
            {
                RunLabel = set.RunLabel,
                GeneratedAtUtc = set.GeneratedAtUtc,
                IsCurrent = set.IsCurrent,
                DayKind = set.DayKind,
                Slips = slips
            });
        }

        var predictionIds = runs
            .SelectMany(run => run.Slips)
            .SelectMany(slip => slip.Selections)
            .Where(selection => selection.PredictionId is > 0)
            .Select(selection => selection.PredictionId!.Value)
            .Distinct()
            .ToList();

        IReadOnlyDictionary<int, Prediction> predictions = new Dictionary<int, Prediction>();
        if (predictionIds.Count > 0)
        {
            predictions = await _context.Predictions
                .AsNoTracking()
                .Where(prediction => predictionIds.Contains(prediction.Id))
                .ToDictionaryAsync(prediction => prediction.Id, ct);
        }

        var selections = runs.SelectMany(run => run.Slips).SelectMany(slip => slip.Selections).ToList();
        var matchDates = selections
            .Select(selection => selection.MatchDateTimeUtc is DateTime kickoffUtc
                ? DateTimeProvider.ConvertUtcToLocalDate(kickoffUtc)
                : date)
            .Append(date)
            .Distinct()
            .ToList();

        var fallbackPredictions = await _context.Predictions
            .AsNoTracking()
            .Where(prediction => matchDates.Contains(prediction.MatchLocalDate) && prediction.IsCurrentRevision)
            .ToListAsync(ct);

        return new BetslipRecordsForDate
        {
            Date = date,
            Section = section,
            Runs = runs,
            PredictionsById = predictions,
            FallbackPredictions = fallbackPredictions
        };
    }

    private async Task<List<SlipDateRow>> ProjectSlipDatesAsync(
        Expression<Func<Betslip, bool>> predicate,
        CancellationToken ct)
    {
        return await _context.Betslips
            .AsNoTracking()
            .Where(predicate)
            .Select(slip => new SlipDateRow(
                slip.SlipNumber,
                slip.TierLabel,
                slip.BetslipSet!.SlipLocalDate))
            .ToListAsync(ct);
    }

    private static bool MatchesSection(SlipDateRow row, BetslipRecordSection section) =>
        BetslipKinds.MatchesSection(
            new Betslip { SlipNumber = row.SlipNumber, TierLabel = row.TierLabel },
            section);

    private readonly record struct SlipDateRow(int SlipNumber, string TierLabel, DateOnly SlipLocalDate);
}
