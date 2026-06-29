using MatchPredictor.Domain.Sourcing;

namespace MatchPredictor.Infrastructure.Services;

/// <summary>
/// Health/circuit-breaker tracker for the FlashScore primary scraper, which previously had no
/// health tracking. After repeated blocks/failures the circuit opens for a cooldown window so the
/// orchestrator can fast-skip the primary source instead of repeatedly hanging on Cloudflare
/// challenge pages.
/// </summary>
public sealed class FlashScoreSourceHealthTracker : ISourceHealthTracker
{
    private readonly object _gate = new();
    private readonly SourceCircuitBreaker _circuitBreaker;
    private SourceCircuitState _circuitState = SourceCircuitState.Closed;
    private SourceHealthSnapshot _snapshot = new() { SourceName = "FlashScore" };

    public FlashScoreSourceHealthTracker()
        : this(new SourceCircuitBreaker(failureThreshold: 3, openDuration: TimeSpan.FromMinutes(10)))
    {
    }

    public FlashScoreSourceHealthTracker(SourceCircuitBreaker circuitBreaker)
    {
        _circuitBreaker = circuitBreaker;
    }

    public string SourceName => "FlashScore";

    public SourceHealthSnapshot GetHealthSnapshot()
    {
        lock (_gate)
        {
            return _snapshot with { };
        }
    }

    public bool IsInCooldown(DateTime utcNow, out TimeSpan remaining)
    {
        lock (_gate)
        {
            if (_circuitBreaker.IsOpen(_circuitState, utcNow))
            {
                remaining = _circuitBreaker.RemainingCooldown(_circuitState, utcNow);
                _snapshot = _snapshot with { Status = "Cooldown", IsCoolingDown = true };
                return true;
            }

            remaining = TimeSpan.Zero;
            if (_snapshot.IsCoolingDown)
            {
                _snapshot = _snapshot with
                {
                    Status = _snapshot.LastMatchCount > 0 ? "Healthy" : "Idle",
                    IsCoolingDown = false,
                    CooldownUntilUtc = null
                };
            }

            return false;
        }
    }

    public void RecordAttempt(string stage, string? detail = null)
    {
        var nowUtc = DateTime.UtcNow;
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                LastAttemptUtc = nowUtc,
                LastStage = stage,
                LastDetail = detail ?? _snapshot.LastDetail
            };
        }
    }

    public void RecordSuccess(string stage, int matchCount, string? detail = null)
    {
        var nowUtc = DateTime.UtcNow;
        lock (_gate)
        {
            _circuitState = _circuitBreaker.RecordSuccess();
            _snapshot = _snapshot with
            {
                Status = "Healthy",
                LastAttemptUtc = nowUtc,
                LastSuccessUtc = nowUtc,
                LastStage = stage,
                LastDetail = detail ?? $"Fetched {matchCount} match(es) from FlashScore.",
                LastMatchCount = matchCount,
                ConsecutiveFailures = 0,
                IsCoolingDown = false,
                CooldownUntilUtc = null
            };
        }
    }

    public void RecordFailure(string stage, string detail)
    {
        var nowUtc = DateTime.UtcNow;
        lock (_gate)
        {
            _circuitState = _circuitBreaker.RecordFailure(_circuitState, nowUtc);
            var open = _circuitBreaker.IsOpen(_circuitState, nowUtc);
            _snapshot = _snapshot with
            {
                Status = open ? "Cooldown" : "Failed",
                LastAttemptUtc = nowUtc,
                LastStage = stage,
                LastDetail = detail,
                ConsecutiveFailures = _circuitState.ConsecutiveFailures,
                IsCoolingDown = open,
                CooldownUntilUtc = _circuitState.OpenUntilUtc
            };
        }
    }

    public void RecordBlocked(string detail)
    {
        RecordFailure("blocked", detail);
    }
}
