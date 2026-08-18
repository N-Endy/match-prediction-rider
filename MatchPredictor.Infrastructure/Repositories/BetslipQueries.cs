using MatchPredictor.Domain.Helpers;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
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

        return await WhereHasSection(_context.BetslipSets.AsNoTracking(), section)
            .Where(s => s.SlipLocalDate >= monthStart && s.SlipLocalDate < monthEnd)
            .Select(s => s.SlipLocalDate)
            .Distinct()
            .OrderBy(d => d)
            .ToListAsync(ct);
    }

    public async Task<DateOnly?> GetLatestSlipDateAsync(
        BetslipRecordSection section,
        CancellationToken ct = default)
    {
        return await WhereHasSection(_context.BetslipSets.AsNoTracking(), section)
            .OrderByDescending(s => s.SlipLocalDate)
            .Select(s => (DateOnly?)s.SlipLocalDate)
            .FirstOrDefaultAsync(ct);
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

        return new BetslipRecordsForDate
        {
            Date = date,
            Section = section,
            Runs = runs,
            PredictionsById = predictions
        };
    }

    private static IQueryable<BetslipSet> WhereHasSection(
        IQueryable<BetslipSet> query,
        BetslipRecordSection section)
    {
        return section switch
        {
            BetslipRecordSection.Rollover => query.Where(set => set.Slips.Any(slip =>
                slip.SlipNumber == BetslipKinds.RolloverSlipNumber ||
                slip.TierLabel.ToLower().StartsWith("rollover"))),
            BetslipRecordSection.Banker => query.Where(set => set.Slips.Any(slip =>
                slip.SlipNumber == BetslipKinds.BankerSlipNumber ||
                slip.TierLabel.ToLower().StartsWith("banker"))),
            BetslipRecordSection.AiDraws => query.Where(set => set.Slips.Any(slip =>
                slip.TierLabel == BetslipKinds.DrawsTierLabel)),
            BetslipRecordSection.Ladder => query.Where(set => set.Slips.Any(slip =>
                slip.SlipNumber != BetslipKinds.RolloverSlipNumber &&
                slip.SlipNumber != BetslipKinds.BankerSlipNumber &&
                !slip.TierLabel.ToLower().StartsWith("rollover") &&
                !slip.TierLabel.ToLower().StartsWith("banker") &&
                slip.TierLabel != BetslipKinds.DrawsTierLabel)),
            _ => query.Where(_ => false)
        };
    }
}
