using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public sealed record LineupImpactAdjustment(
    double AttackModifier,
    double DefenseModifier,
    IReadOnlyList<string> DetectedMissingStarters,
    string Explanation);

public interface ILineupAvailabilityService
{
    Task<MatchLineupSnapshot?> GetLatestLineupSnapshotAsync(string fixtureKey, CancellationToken ct = default);

    Task<MatchLineupSnapshot> RecordLineupSnapshotAsync(MatchLineupSnapshot snapshot, CancellationToken ct = default);

    LineupImpactAdjustment CalculateLineupAdjustment(
        IReadOnlyList<string> confirmedStarters,
        IReadOnlyList<string> expectedStarters,
        IReadOnlyList<string> missingKeyPlayers);

    Task ProcessUpcomingConfirmedLineupsAsync(int lookaheadMinutes = 75, CancellationToken ct = default);
}
