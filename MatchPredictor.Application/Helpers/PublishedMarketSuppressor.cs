using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.EntityFrameworkCore;

namespace MatchPredictor.Application.Helpers;

/// <summary>
/// Soft-suppresses prediction categories with persistently negative ROI and/or CLV.
/// Used at publish time and again when packing betslips.
/// </summary>
public static class PublishedMarketSuppressor
{
    public sealed record SuppressDecision(
        string Category,
        bool Suppress,
        double RoiPercent,
        double? AverageClvPercent,
        int SettledCount,
        int ClvSampleCount,
        string Reason);

    public static async Task<IReadOnlyDictionary<string, SuppressDecision>> EvaluateAsync(
        ApplicationDbContext dbContext,
        PredictionSettings settings,
        CancellationToken cancellationToken = default)
    {
        var minSettled = Math.Max(5, settings.SuppressPublishMinSettledBets);
        var roiFloor = settings.SuppressPublishBelowRoiPercent;
        var clvFloor = settings.SuppressPublishBelowClvPercent;
        var lookbackStart = DateTimeProvider.GetLocalDate().AddDays(-21);

        var recent = await (
            from prediction in dbContext.Predictions.AsNoTracking()
            join publish in dbContext.PredictionOddsSnapshots.AsNoTracking()
                on prediction.Id equals publish.PredictionId
            where prediction.WasPublished &&
                  prediction.IsCurrentRevision &&
                  prediction.MatchLocalDate >= lookbackStart &&
                  (prediction.ActualScore != null || prediction.ActualOutcome != null) &&
                  publish.SnapshotKind == PredictionOddsSnapshotKind.Publish &&
                  publish.DecimalOdds > 1d
            select new
            {
                prediction.Id,
                prediction.PredictionCategory,
                prediction.PredictedOutcome,
                prediction.ActualOutcome,
                PublishOdds = publish.DecimalOdds
            }).ToListAsync(cancellationToken);

        if (recent.Count == 0)
        {
            return new Dictionary<string, SuppressDecision>(StringComparer.OrdinalIgnoreCase);
        }

        var predictionIds = recent.Select(row => row.Id).Distinct().ToList();
        var closeByPredictionId = await dbContext.PredictionOddsSnapshots
            .AsNoTracking()
            .Where(snapshot =>
                predictionIds.Contains(snapshot.PredictionId) &&
                snapshot.SnapshotKind == PredictionOddsSnapshotKind.Close &&
                snapshot.DecimalOdds > 1d)
            .GroupBy(snapshot => snapshot.PredictionId)
            .Select(group => new
            {
                PredictionId = group.Key,
                DecimalOdds = group.OrderByDescending(row => row.CapturedAtUtc).Select(row => row.DecimalOdds).First()
            })
            .ToDictionaryAsync(row => row.PredictionId, row => row.DecimalOdds, cancellationToken);

        return recent
            .GroupBy(row => row.PredictionCategory, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var rows = group.ToList();
                var staked = rows.Count;
                var returned = rows.Sum(row =>
                    string.Equals(row.ActualOutcome, row.PredictedOutcome, StringComparison.OrdinalIgnoreCase)
                        ? row.PublishOdds
                        : 0d);
                var roiPercent = staked > 0 ? ((returned - staked) / staked) * 100d : 0d;

                var clvSamples = rows
                    .Select(row =>
                    {
                        if (!closeByPredictionId.TryGetValue(row.Id, out var closeOdds))
                        {
                            return (double?)null;
                        }

                        var clv = BetPricingMath.CalculateClosingLineValuePercent(row.PublishOdds, closeOdds);
                        return clv.HasValue ? clv.Value * 100d : null;
                    })
                    .Where(value => value.HasValue)
                    .Select(value => value!.Value)
                    .ToList();
                var averageClv = clvSamples.Count > 0 ? clvSamples.Average() : (double?)null;

                var roiSuppress = staked >= minSettled && roiPercent < roiFloor;
                var clvSuppress = averageClv.HasValue &&
                                 clvSamples.Count >= minSettled &&
                                 averageClv.Value < clvFloor;

                var reasons = new List<string>();
                if (roiSuppress)
                {
                    reasons.Add($"ROI {roiPercent:F1}% < {roiFloor:F1}%");
                }

                if (clvSuppress)
                {
                    reasons.Add($"CLV {averageClv:F1}% < {clvFloor:F1}%");
                }

                return new SuppressDecision(
                    group.Key,
                    roiSuppress || clvSuppress,
                    roiPercent,
                    averageClv,
                    staked,
                    clvSamples.Count,
                    string.Join("; ", reasons));
            })
            .Where(decision => decision.Suppress)
            .ToDictionary(decision => decision.Category, decision => decision, StringComparer.OrdinalIgnoreCase);
    }
}
