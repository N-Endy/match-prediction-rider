namespace MatchPredictor.Domain.Sourcing;

/// <summary>
/// A point-in-time view of a data source's health, suitable for surfacing on the health page and
/// for orchestration decisions (skip a source that is in cooldown).
/// </summary>
public sealed record SourceHealthSnapshot
{
    public string SourceName { get; init; } = string.Empty;
    public string Status { get; init; } = "Idle";
    public DateTime? LastAttemptUtc { get; init; }
    public DateTime? LastSuccessUtc { get; init; }
    public string? LastStage { get; init; }
    public string? LastDetail { get; init; }
    public int LastMatchCount { get; init; }
    public int ConsecutiveFailures { get; init; }
    public bool IsCoolingDown { get; init; }
    public DateTime? CooldownUntilUtc { get; init; }
}

/// <summary>
/// Common surface for a score data source's health/circuit-breaker tracker, allowing the
/// orchestrator to consult and record source health uniformly across providers.
/// </summary>
public interface ISourceHealthTracker
{
    string SourceName { get; }

    bool IsInCooldown(DateTime utcNow, out TimeSpan remaining);

    void RecordAttempt(string stage, string? detail = null);

    void RecordSuccess(string stage, int matchCount, string? detail = null);

    void RecordFailure(string stage, string detail);

    void RecordBlocked(string detail);

    SourceHealthSnapshot GetHealthSnapshot();
}
