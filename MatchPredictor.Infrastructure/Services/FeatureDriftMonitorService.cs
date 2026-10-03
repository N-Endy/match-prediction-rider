using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatchPredictor.Infrastructure.Services;

public sealed class FeatureDriftMonitorService : IFeatureDriftMonitorService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<FeatureDriftMonitorService> _logger;

    public FeatureDriftMonitorService(
        ApplicationDbContext db,
        ILogger<FeatureDriftMonitorService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<FeatureDriftReport> ComputeDriftReportAsync(
        int referenceWindowDays = 90,
        int currentWindowDays = 30,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var currentCutoff = now.AddDays(-currentWindowDays);
        var referenceCutoff = now.AddDays(-referenceWindowDays);

        var snapshots = await _db.FixtureFeatureSnapshots
            .AsNoTracking()
            .Where(s => s.CapturedAtUtc >= referenceCutoff)
            .OrderBy(s => s.CapturedAtUtc)
            .ToListAsync(ct);

        var refSnapshots = snapshots.Where(s => s.CapturedAtUtc < currentCutoff).ToList();
        var curSnapshots = snapshots.Where(s => s.CapturedAtUtc >= currentCutoff).ToList();

        _logger.LogInformation(
            "Computing feature drift across {RefTotal} reference snapshots and {CurTotal} current snapshots.",
            refSnapshots.Count,
            curSnapshots.Count);

        var results = new List<FeatureDriftResult>();

        void EvaluateMetric(string featureName, Func<FixtureFeatureSnapshot, double?> selector)
        {
            var refVals = refSnapshots.Select(selector).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            var curVals = curSnapshots.Select(selector).Where(v => v.HasValue).Select(v => v!.Value).ToList();

            if (refVals.Count > 0 && curVals.Count > 0)
            {
                results.Add(FeatureDriftMath.EvaluateFeature(featureName, refVals, curVals));
            }
        }

        EvaluateMetric("HomeExpectedGoalsFor", s => s.HomeExpectedGoalsFor);
        EvaluateMetric("AwayExpectedGoalsFor", s => s.AwayExpectedGoalsFor);
        EvaluateMetric("HomeFormGoalsForPerMatch", s => s.HomeFormGoalsForPerMatch);
        EvaluateMetric("AwayFormGoalsForPerMatch", s => s.AwayFormGoalsForPerMatch);
        EvaluateMetric("HomeFormPointsPerMatch", s => s.HomeFormPointsPerMatch);
        EvaluateMetric("AwayFormPointsPerMatch", s => s.AwayFormPointsPerMatch);
        EvaluateMetric("HomeRestDays", s => s.HomeRestDays);
        EvaluateMetric("AwayRestDays", s => s.AwayRestDays);

        var hasSignificant = results.Any(r => r.Severity == DriftSeverity.Significant);
        var hasModerate = results.Any(r => r.Severity == DriftSeverity.Moderate);

        var recommendedAction = hasSignificant
            ? "Significant covariate shift detected. Immediate champion/challenger retraining recommended."
            : hasModerate
                ? "Moderate drift observed in one or more features. Continue shadow monitoring."
                : "All feature distributions are stable.";

        return new FeatureDriftReport(
            GeneratedAtUtc: now,
            ReferenceWindowDays: referenceWindowDays,
            CurrentWindowDays: currentWindowDays,
            ReferenceSnapshotCount: refSnapshots.Count,
            CurrentSnapshotCount: curSnapshots.Count,
            FeatureResults: results,
            HasSignificantDrift: hasSignificant,
            HasModerateDrift: hasModerate,
            RecommendedAction: recommendedAction);
    }
}
