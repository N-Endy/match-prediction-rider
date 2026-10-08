using MatchPredictor.Application.Helpers;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatchPredictor.Application.Services;

public sealed class MarketTimingService : IMarketTimingService
{
    private readonly ApplicationDbContext _db;
    private readonly ISourceMarketPricingService _sourceMarketPricingService;
    private readonly ILogger<MarketTimingService> _logger;

    public MarketTimingService(
        ApplicationDbContext db,
        ISourceMarketPricingService sourceMarketPricingService,
        ILogger<MarketTimingService> logger)
    {
        _db = db;
        _sourceMarketPricingService = sourceMarketPricingService;
        _logger = logger;
    }

    public async Task<MarketTimingAdvisory> AnalyzeMarketTimingAsync(
        int predictionId,
        CancellationToken ct = default)
    {
        var prediction = await _db.Predictions
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == predictionId, ct);

        if (prediction == null)
        {
            return CreateDefaultStableAdvisory(0.0);
        }

        var latestOddsSnapshot = await _db.PredictionOddsSnapshots
            .AsNoTracking()
            .Where(s => s.PredictionId == predictionId)
            .OrderByDescending(s => s.CapturedAtUtc)
            .FirstOrDefaultAsync(ct);

        var currentOdds = latestOddsSnapshot?.DecimalOdds ?? 2.0;
        var matchUtc = prediction.MatchDateTime ?? DateTime.UtcNow;

        return await EvaluateOddsTrajectoryAsync(
            prediction.FixtureKey,
            prediction.PredictionCategory,
            prediction.PredictedOutcome,
            currentOdds,
            matchUtc,
            ct);
    }

    public async Task<MarketTimingAdvisory> EvaluateOddsTrajectoryAsync(
        string fixtureKey,
        string market,
        string outcome,
        double currentOdds,
        DateTime matchDateTimeUtc,
        CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(fixtureKey) || currentOdds <= 1.0)
        {
            return CreateDefaultStableAdvisory(currentOdds);
        }

        // Query historical odds snapshots for this fixture
        var snapshots = await _db.PredictionOddsSnapshots
            .AsNoTracking()
            .Where(s => s.Market == market && s.Outcome == outcome)
            .Where(s => _db.Predictions.Any(p => p.Id == s.PredictionId && p.FixtureKey == fixtureKey))
            .OrderBy(s => s.CapturedAtUtc)
            .ToListAsync(ct);

        if (snapshots.Count == 0)
        {
            return CreateDefaultStableAdvisory(currentOdds);
        }

        var baselineSnapshot = snapshots.First();
        var baselineOdds = baselineSnapshot.DecimalOdds;
        var baselineTime = baselineSnapshot.CapturedAtUtc;

        var hoursElapsed = Math.Max(0.1, (nowUtc - baselineTime).TotalHours);
        var oddsDelta = currentOdds - baselineOdds;
        var velocityPerHour = Math.Round(oddsDelta / hoursElapsed, 4);

        var baselineImpliedProb = 1.0 / baselineOdds;
        var currentImpliedProb = 1.0 / currentOdds;
        var probChange = currentImpliedProb - baselineImpliedProb;
        var probChangePercent = Math.Round(probChange * 100.0, 2);

        // Check if confirmed lineup exists for this fixture
        var hasConfirmedLineup = await _db.MatchLineupSnapshots
            .AsNoTracking()
            .AnyAsync(s => s.FixtureKey == fixtureKey && s.IsConfirmed, ct);

        // Steam Move detection: price crashed sharply (e.g. odds dropped by >= 0.12 or implied probability rose by >= 3.5%)
        var isSteamDrop = probChange >= 0.035 && oddsDelta <= -0.12;

        if (isSteamDrop)
        {
            var dropAmount = Math.Abs(oddsDelta);
            string summary;

            if (hasConfirmedLineup)
            {
                summary = $"⚡ Lineup-Driven Steam Move! Price collapsed from {baselineOdds:F2} to {currentOdds:F2} (-{dropAmount:F2}) following confirmed starting XI. Take current line immediately before edge evaporates.";
            }
            else
            {
                summary = $"⚡ Sharp Steam Move! Syndicate money shortening price from {baselineOdds:F2} to {currentOdds:F2} (-{dropAmount:F2}, velocity {velocityPerHour:F2}/hr). Take now before edge disappears.";
            }

            return new MarketTimingAdvisory(
                Signal: TimingSignal.UrgentTakeNow,
                CurrentOdds: currentOdds,
                OpeningOdds: baselineOdds,
                OddsVelocityPerHour: velocityPerHour,
                ProbabilityChangePercent: probChangePercent,
                Summary: summary,
                IsSteamMove: true,
                IsLineupDriven: hasConfirmedLineup,
                EvaluatedAtUtc: nowUtc);
        }

        // Drifting line detection: price lengthening
        if (oddsDelta >= 0.15 && velocityPerHour > 0.01)
        {
            var riseAmount = oddsDelta;
            var summary = $"📈 Drifting Price (+{riseAmount:F2}, now {currentOdds:F2} vs {baselineOdds:F2} open). Value expanding; consider waiting closer to kickoff for peak price.";

            return new MarketTimingAdvisory(
                Signal: TimingSignal.DriftingWait,
                CurrentOdds: currentOdds,
                OpeningOdds: baselineOdds,
                OddsVelocityPerHour: velocityPerHour,
                ProbabilityChangePercent: probChangePercent,
                Summary: summary,
                IsSteamMove: false,
                IsLineupDriven: hasConfirmedLineup,
                EvaluatedAtUtc: nowUtc);
        }

        // Stable
        return new MarketTimingAdvisory(
            Signal: TimingSignal.Stable,
            CurrentOdds: currentOdds,
            OpeningOdds: baselineOdds,
            OddsVelocityPerHour: velocityPerHour,
            ProbabilityChangePercent: probChangePercent,
            Summary: $"⚖️ Stable Market Price (moved {oddsDelta:+0.00;-0.00;0.00} over {hoursElapsed:F1}h). Line is mature and efficient.",
            IsSteamMove: false,
            IsLineupDriven: hasConfirmedLineup,
            EvaluatedAtUtc: nowUtc);
    }

    public async Task CaptureInterimOddsSnapshotsAsync(CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var windowStartUtc = nowUtc.AddHours(1);
        var windowEndUtc = nowUtc.AddHours(24);

        // Find active published predictions kicking off between 1h and 24h
        var upcomingPredictions = await _db.Predictions
            .AsNoTracking()
            .Where(p => p.IsCurrentRevision && p.WasPublished)
            .Where(p => p.MatchDateTime.HasValue &&
                        p.MatchDateTime.Value >= windowStartUtc &&
                        p.MatchDateTime.Value <= windowEndUtc)
            .OrderBy(p => p.MatchDateTime)
            .ToListAsync(ct);

        if (upcomingPredictions.Count == 0)
        {
            return;
        }

        var cutoffRecent = nowUtc.AddHours(-2);
        var recentSnapshotPredictionIds = await _db.PredictionOddsSnapshots
            .AsNoTracking()
            .Where(s => s.CapturedAtUtc >= cutoffRecent &&
                        s.SnapshotKind == PredictionOddsSnapshotKind.Interim)
            .Select(s => s.PredictionId)
            .Distinct()
            .ToListAsync(ct);

        var predictionsToSnapshot = upcomingPredictions
            .Where(p => !recentSnapshotPredictionIds.Contains(p.Id))
            .DistinctBy(p => p.Id)
            .ToList();

        if (predictionsToSnapshot.Count == 0)
        {
            return;
        }

        IReadOnlyList<SourceMarketFixture> sourceFixtures = [];
        try
        {
            sourceFixtures = await _sourceMarketPricingService.GetTodaySourceMarketFixturesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load live source pricing for interim odds snapshots.");
            return;
        }

        if (sourceFixtures.Count == 0)
        {
            return;
        }

        var seenPredictionIds = new HashSet<int>();
        var newSnapshots = new List<PredictionOddsSnapshot>();
        foreach (var pred in predictionsToSnapshot)
        {
            if (!seenPredictionIds.Add(pred.Id))
            {
                continue;
            }

            var matchFixture = SourceMarketFixtureMatcher.FindBestFixture(
                sourceFixtures, pred.HomeTeam, pred.AwayTeam, pred.League, pred.MatchDateTime);

            if (matchFixture == null)
            {
                continue;
            }

            var odds = ResolveOddsForPrediction(matchFixture, pred.PredictionCategory, pred.PredictedOutcome);
            if (odds.HasValue && odds.Value > 1.0)
            {
                newSnapshots.Add(new PredictionOddsSnapshot
                {
                    PredictionId = pred.Id,
                    PredictionRunId = pred.PredictionRunId,
                    SourceName = "SportyBet",
                    Market = pred.PredictionCategory,
                    Outcome = pred.PredictedOutcome,
                    DecimalOdds = odds.Value,
                    ImpliedProbability = Math.Round(1.0 / odds.Value, 6),
                    OddsDerivationSource = "LiveInterim",
                    SnapshotKind = PredictionOddsSnapshotKind.Interim,
                    CapturedAtUtc = nowUtc
                });
            }
        }

        if (newSnapshots.Count > 0)
        {
            try
            {
                _db.PredictionOddsSnapshots.AddRange(newSnapshots);
                await _db.SaveChangesAsync(ct);
                _logger.LogInformation("Saved {Count} interim odds snapshots across {FixtureCount} fixtures.",
                    newSnapshots.Count, predictionsToSnapshot.Count);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Failed to save {Count} interim odds snapshots due to database update exception.", newSnapshots.Count);
                throw;
            }
        }
    }

    private static double? ResolveOddsForPrediction(SourceMarketFixture fixture, string category, string outcome)
    {
        return category switch
        {
            "StraightWin" when outcome.Contains("Home", StringComparison.OrdinalIgnoreCase) => fixture.HomeWinOdds,
            "StraightWin" when outcome.Contains("Away", StringComparison.OrdinalIgnoreCase) => fixture.AwayWinOdds,
            "Draw" => fixture.DrawOdds,
            "Over25Goals" or "Over2" => fixture.Over25Odds,
            "Under25Goals" or "Under2" => fixture.Under25Odds,
            "BTTS" when outcome.Contains("Yes", StringComparison.OrdinalIgnoreCase) => fixture.BttsYesOdds,
            "BTTS" when outcome.Contains("No", StringComparison.OrdinalIgnoreCase) => fixture.BttsNoOdds,
            _ => null
        };
    }

    private static MarketTimingAdvisory CreateDefaultStableAdvisory(double currentOdds)
    {
        return new MarketTimingAdvisory(
            Signal: TimingSignal.Stable,
            CurrentOdds: Math.Max(1.0, currentOdds),
            OpeningOdds: currentOdds > 1.0 ? currentOdds : null,
            OddsVelocityPerHour: 0.0,
            ProbabilityChangePercent: 0.0,
            Summary: "⚖️ Stable Price. No significant odds movement detected.",
            IsSteamMove: false,
            IsLineupDriven: false,
            EvaluatedAtUtc: DateTime.UtcNow);
    }
}
