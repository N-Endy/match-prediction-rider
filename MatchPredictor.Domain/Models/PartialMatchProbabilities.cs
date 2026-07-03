namespace MatchPredictor.Domain.Models;

/// <summary>
/// A per-market probability signal where any market may be missing. Used for signals
/// (e.g. real bookmaker pricing) that do not always quote every market for a fixture.
/// Missing markets are simply skipped by the ensemble blender.
/// </summary>
public sealed record PartialMatchProbabilities(
    double? Btts = null,
    double? Over25 = null,
    double? Under25 = null,
    double? Draw = null,
    double? HomeWin = null,
    double? AwayWin = null)
{
    public bool HasAnySignal =>
        Btts.HasValue || Over25.HasValue || Under25.HasValue ||
        Draw.HasValue || HomeWin.HasValue || AwayWin.HasValue;
}
