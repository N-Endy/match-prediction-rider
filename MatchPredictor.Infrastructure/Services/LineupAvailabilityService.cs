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
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply lineup adjustment to forecast observation {Id} for fixture {FixtureKey}",
                    observation.Id, observation.FixtureKey);
            }
        }

        await _db.SaveChangesAsync(ct);
    }
}
