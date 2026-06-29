namespace MatchPredictor.Domain.Sourcing;

/// <summary>
/// Immutable circuit state for a single data source.
/// </summary>
public sealed record SourceCircuitState(int ConsecutiveFailures, DateTime? OpenUntilUtc)
{
    public static SourceCircuitState Closed { get; } = new(0, null);
}

/// <summary>
/// Pure, deterministic circuit breaker for a data source. After a threshold of consecutive
/// failures it "opens" for a cooldown window so the orchestrator can fast-skip a source that is
/// being blocked (e.g. Cloudflare challenge) instead of hanging on it every cycle. A single
/// success closes the circuit again.
/// </summary>
public sealed class SourceCircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;

    public SourceCircuitBreaker(int failureThreshold = 3, TimeSpan? openDuration = null)
    {
        if (failureThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(failureThreshold), "Failure threshold must be at least 1.");
        }

        _failureThreshold = failureThreshold;
        _openDuration = openDuration ?? TimeSpan.FromMinutes(10);
    }

    public int FailureThreshold => _failureThreshold;

    public TimeSpan OpenDuration => _openDuration;

    public SourceCircuitState RecordSuccess() => SourceCircuitState.Closed;

    public SourceCircuitState RecordFailure(SourceCircuitState state, DateTime utcNow)
    {
        var failures = state.ConsecutiveFailures + 1;
        var openUntil = failures >= _failureThreshold
            ? utcNow.Add(_openDuration)
            : state.OpenUntilUtc;
        return new SourceCircuitState(failures, openUntil);
    }

    public bool IsOpen(SourceCircuitState state, DateTime utcNow)
    {
        return state.OpenUntilUtc.HasValue && state.OpenUntilUtc.Value > utcNow;
    }

    public TimeSpan RemainingCooldown(SourceCircuitState state, DateTime utcNow)
    {
        if (!IsOpen(state, utcNow))
        {
            return TimeSpan.Zero;
        }

        return state.OpenUntilUtc!.Value - utcNow;
    }
}
