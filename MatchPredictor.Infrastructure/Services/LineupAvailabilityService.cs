using System.Text.Json;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatchPredictor.Infrastructure.Services;

public sealed class LineupAvailabilityService : ILineupAvailabilityService
{
    private const double MaxAttackAdjustment = 0.25;
    private const double MaxDefenseAdjustment = 0.25;
    private const double PerMissingStarPenalty = 0.08;
    private const double PerMissingStarterDefensePenalty = 0.06;

    private readonly ApplicationDbContext _db;
    private readonly ILogger<LineupAvailabilityService> _logger;

    public LineupAvailabilityService(
        ApplicationDbContext db,
        ILogger<LineupAvailabilityService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<MatchLineupSnapshot?> GetLatestLineupSnapshotAsync(string fixtureKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fixtureKey))
        {
            return null;
        }

        return await _db.MatchLineupSnapshots
            .AsNoTracking()
            .Where(s => s.FixtureKey == fixtureKey)
            .OrderByDescending(s => s.CapturedAtUtc)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<MatchLineupSnapshot> RecordLineupSnapshotAsync(MatchLineupSnapshot snapshot, CancellationToken ct = default)
    {
        _db.MatchLineupSnapshots.Add(snapshot);
        await _db.SaveChangesAsync(ct);
        return snapshot;
    }

    public LineupImpactAdjustment CalculateLineupAdjustment(
        IReadOnlyList<string> confirmedStarters,
        IReadOnlyList<string> expectedStarters,
        IReadOnlyList<string> missingKeyPlayers)
    {
        var detectedMissing = new List<string>();

        if (missingKeyPlayers != null && missingKeyPlayers.Count > 0)
        {
            detectedMissing.AddRange(missingKeyPlayers);
        }

        if (confirmedStarters != null && confirmedStarters.Count > 0 &&
            expectedStarters != null && expectedStarters.Count > 0)
        {
            var confirmedSet = confirmedStarters
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim().ToLowerInvariant())
                .ToHashSet();

            foreach (var expected in expectedStarters)
            {
                if (string.IsNullOrWhiteSpace(expected))
                {
                    continue;
                }

                var normalized = expected.Trim().ToLowerInvariant();
                if (!confirmedSet.Contains(normalized) &&
                    !detectedMissing.Any(m => m.Equals(expected, StringComparison.OrdinalIgnoreCase)))
                {
                    detectedMissing.Add(expected.Trim());
                }
            }
        }

        if (detectedMissing.Count == 0)
        {
            return new LineupImpactAdjustment(0.0, 0.0, [], "Full regular starting lineup confirmed.");
        }

        var rawAttackDelta = -(detectedMissing.Count * PerMissingStarPenalty);
        var rawDefenseDelta = +(detectedMissing.Count * PerMissingStarterDefensePenalty);

        var clampedAttack = Math.Clamp(rawAttackDelta, -MaxAttackAdjustment, MaxAttackAdjustment);
        var clampedDefense = Math.Clamp(rawDefenseDelta, -MaxDefenseAdjustment, MaxDefenseAdjustment);

        var missingList = string.Join(", ", detectedMissing.Take(3));
        var explanation = $"{detectedMissing.Count} key starter(s) missing ({missingList}). Net attack adjusted by {clampedAttack:P1}.";

        return new LineupImpactAdjustment(
            clampedAttack,
            clampedDefense,
            detectedMissing,
            explanation);
    }

    public async Task ProcessUpcomingConfirmedLineupsAsync(int lookaheadMinutes = 75, CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var windowEndUtc = nowUtc.AddMinutes(Math.Clamp(lookaheadMinutes, 15, 120));

        var upcomingObservations = await _db.ForecastObservations
            .Where(p => p.IsCurrentRevision && p.IsPublished)
            .Where(p => p.MatchDateTime.HasValue &&
                        p.MatchDateTime.Value >= nowUtc &&
                        p.MatchDateTime.Value <= windowEndUtc)
            .ToListAsync(ct);

        if (upcomingObservations.Count == 0)
        {
            return;
        }

        var fixtureKeys = upcomingObservations.Select(p => p.FixtureKey).Distinct().ToList();
        var lineupSnapshots = await _db.MatchLineupSnapshots
            .AsNoTracking()
            .Where(s => fixtureKeys.Contains(s.FixtureKey) && s.IsConfirmed)
            .OrderByDescending(s => s.CapturedAtUtc)
            .ToListAsync(ct);

        var latestLineups = lineupSnapshots
            .GroupBy(s => s.FixtureKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var observation in upcomingObservations)
        {
            if (!latestLineups.TryGetValue(observation.FixtureKey, out var lineup))
            {
                continue;
            }

            try
            {
                var contributions = new Dictionary<string, object>();
                if (!string.IsNullOrWhiteSpace(observation.FeatureContributionsJson) &&
                    observation.FeatureContributionsJson != "{}")
                {
                    try
                    {
                        var parsed = JsonSerializer.Deserialize<Dictionary<string, object>>(observation.FeatureContributionsJson);
                        if (parsed != null)
                        {
                            contributions = parsed;
                        }
                    }
                    catch (JsonException) { }
                }

                contributions["lineupConfirmed"] = true;
                contributions["lineupAdjusted"] = true;
                contributions["homeAttackAdjustment"] = Math.Round(lineup.HomeAttackAdjustment, 4);
                contributions["homeDefenseAdjustment"] = Math.Round(lineup.HomeDefenseAdjustment, 4);
                contributions["awayAttackAdjustment"] = Math.Round(lineup.AwayAttackAdjustment, 4);
                contributions["awayDefenseAdjustment"] = Math.Round(lineup.AwayDefenseAdjustment, 4);

                if (contributions.TryGetValue("isSteamMove", out var steamFlag) &&
                    steamFlag is JsonElement elem && elem.ValueKind == JsonValueKind.True)
                {
                    contributions["isLineupDrivenSteam"] = true;
                }

                observation.FeatureContributionsJson = JsonSerializer.Serialize(contributions);

                var netHomeAdvantage = (lineup.HomeAttackAdjustment - lineup.AwayDefenseAdjustment)
                                     - (lineup.AwayAttackAdjustment - lineup.HomeDefenseAdjustment);
                var netGoalShift = (lineup.HomeAttackAdjustment + lineup.AwayAttackAdjustment)
                                 + (lineup.HomeDefenseAdjustment + lineup.AwayDefenseAdjustment);

                var probShift = observation.Market switch
                {
                    PredictionMarket.HomeWin => netHomeAdvantage * 0.15,
                    PredictionMarket.AwayWin => -netHomeAdvantage * 0.15,
                    PredictionMarket.Over25Goals => netGoalShift * 0.10,
                    PredictionMarket.Under25Goals => -netGoalShift * 0.10,
                    PredictionMarket.BothTeamsScore => Math.Min(lineup.HomeAttackAdjustment, lineup.AwayAttackAdjustment) * 0.10,
                    PredictionMarket.Draw => -Math.Abs(netHomeAdvantage) * 0.08,
                    _ => 0.0
                };

                if (Math.Abs(probShift) > 0.0001)
                {
                    if (observation.CorrectedProbability > 0)
                    {
                        observation.CorrectedProbability = Math.Clamp(observation.CorrectedProbability + probShift, 0.02, 0.98);
                    }
                    if (observation.CalibratedProbability > 0)
                    {
                        observation.CalibratedProbability = Math.Clamp(observation.CalibratedProbability + probShift, 0.02, 0.98);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply lineup adjustment to forecast observation {Id} for fixture {FixtureKey}",
                    observation.Id, observation.FixtureKey);
            }
        }

        NormalizeUpcomingObservations(upcomingObservations);

        var matchingPredictions = await _db.Predictions
            .Where(p => fixtureKeys.Contains(p.FixtureKey) && p.IsCurrentRevision)
            .ToListAsync(ct);

        foreach (var observation in upcomingObservations)
        {
            var matchingPred = matchingPredictions.FirstOrDefault(p =>
                p.FixtureKey == observation.FixtureKey &&
                PredictionMarketExtensions.TryFromCategory(p.PredictionCategory, out var m) &&
                m == observation.Market);

            if (matchingPred != null && observation.CalibratedProbability > 0)
            {
                matchingPred.ConfidenceScore = (decimal)observation.CalibratedProbability;
                if (observation.CorrectedProbability > 0)
                {
                    matchingPred.RawConfidenceScore = (decimal)observation.CorrectedProbability;
                }
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    private static void NormalizeUpcomingObservations(List<ForecastObservation> observations)
    {
        foreach (var group in observations.GroupBy(o => o.FixtureKey))
        {
            var obsList = group.ToList();
            var home = obsList.FirstOrDefault(o => o.Market == PredictionMarket.HomeWin);
            var draw = obsList.FirstOrDefault(o => o.Market == PredictionMarket.Draw);
            var away = obsList.FirstOrDefault(o => o.Market == PredictionMarket.AwayWin);

            if (home != null && draw != null && away != null)
            {
                var sum = home.CalibratedProbability + draw.CalibratedProbability + away.CalibratedProbability;
                if (sum > 0)
                {
                    home.CalibratedProbability = Math.Clamp(home.CalibratedProbability / sum, 0.01, 0.98);
                    draw.CalibratedProbability = Math.Clamp(draw.CalibratedProbability / sum, 0.01, 0.98);
                    away.CalibratedProbability = Math.Clamp(1.0 - home.CalibratedProbability - draw.CalibratedProbability, 0.01, 0.98);
                }
            }

            var over = obsList.FirstOrDefault(o => o.Market == PredictionMarket.Over25Goals);
            var under = obsList.FirstOrDefault(o => o.Market == PredictionMarket.Under25Goals);
            if (over != null && under != null)
            {
                var sum = over.CalibratedProbability + under.CalibratedProbability;
                if (sum > 0)
                {
                    over.CalibratedProbability = Math.Clamp(over.CalibratedProbability / sum, 0.01, 0.98);
                    under.CalibratedProbability = Math.Clamp(1.0 - over.CalibratedProbability, 0.01, 0.98);
                }
            }
        }
    }
}
