namespace MatchPredictor.Domain.Models;

public enum TimingSignal
{
    Stable = 1,
    UrgentTakeNow = 2,
    DriftingWait = 3,
    SharpContrarian = 4
}

/// <summary>
/// Advisory recommendation regarding market odds trajectory, price discovery velocity,
/// and optimal bet-timing window.
/// </summary>
public sealed record MarketTimingAdvisory(
    TimingSignal Signal,
    double CurrentOdds,
    double? OpeningOdds,
    double OddsVelocityPerHour,
    double ProbabilityChangePercent,
    string Summary,
    bool IsSteamMove,
    bool IsLineupDriven,
    DateTime EvaluatedAtUtc);
