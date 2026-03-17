namespace MatchPredictor.Infrastructure.Services;

public sealed class AiScoreSourceHealthTracker
{
    private readonly object _gate = new();
    private AiScoreSourceHealthSnapshot _snapshot = AiScoreSourceHealthSnapshot.CreateDefault();
    private int _lastSuccessfulMatchCount;

    public AiScoreSourceHealthSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return _snapshot with { };
        }
    }

    public int GetLastSuccessfulMatchCount()
    {
        lock (_gate)
        {
            return _lastSuccessfulMatchCount;
        }
    }

    public bool IsInCooldown(DateTime utcNow, out TimeSpan remaining)
    {
        lock (_gate)
        {
            if (_snapshot.CooldownUntilUtc.HasValue && _snapshot.CooldownUntilUtc.Value > utcNow)
            {
                remaining = _snapshot.CooldownUntilUtc.Value - utcNow;
                _snapshot = _snapshot with
                {
                    Status = "Cooldown",
                    IsCoolingDown = true
                };
                return true;
            }

            remaining = TimeSpan.Zero;
            if (_snapshot.IsCoolingDown)
            {
                _snapshot = _snapshot with
                {
                    Status = _lastSuccessfulMatchCount > 0 ? "Healthy" : "Idle",
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
            _lastSuccessfulMatchCount = matchCount;
            _snapshot = _snapshot with
            {
                Status = "Healthy",
                LastAttemptUtc = nowUtc,
                LastSuccessUtc = nowUtc,
                LastStage = stage,
                LastDetail = detail ?? $"Fetched {matchCount} match(es) from AiScore.",
                LastMatchCount = matchCount,
                LastFallbackMatchCount = 0,
                LastSupplementMatchCount = 0,
                IsCoolingDown = false,
                CooldownUntilUtc = null
            };
        }
    }

    public void RecordHttpBlocked(string detail)
    {
        var nowUtc = DateTime.UtcNow;
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                Status = "HttpBlocked",
                LastAttemptUtc = nowUtc,
                LastStage = "http",
                LastDetail = detail
            };
        }
    }

    public void RecordBrowserBlocked(string detail, TimeSpan cooldown)
    {
        var nowUtc = DateTime.UtcNow;
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                Status = "Blocked",
                LastAttemptUtc = nowUtc,
                LastStage = "browser",
                LastDetail = detail,
                IsCoolingDown = true,
                CooldownUntilUtc = nowUtc.Add(cooldown)
            };
        }
    }

    public void RecordEmpty(string stage, string detail)
    {
        var nowUtc = DateTime.UtcNow;
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                Status = "Empty",
                LastAttemptUtc = nowUtc,
                LastStage = stage,
                LastDetail = detail,
                LastMatchCount = 0
            };
        }
    }

    public void RecordFailure(string stage, string detail)
    {
        var nowUtc = DateTime.UtcNow;
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                Status = "Failed",
                LastAttemptUtc = nowUtc,
                LastStage = stage,
                LastDetail = detail
            };
        }
    }

    public void RecordFallback(string stage, int matchCount, string detail)
    {
        var nowUtc = DateTime.UtcNow;
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                Status = _snapshot.IsCoolingDown ? "CooldownFallback" : "Fallback",
                LastAttemptUtc = nowUtc,
                LastStage = stage,
                LastDetail = detail,
                LastFallbackMatchCount = matchCount
            };
        }
    }

    public void RecordSupplement(int matchCount, string detail)
    {
        var nowUtc = DateTime.UtcNow;
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                Status = "Supplemented",
                LastAttemptUtc = nowUtc,
                LastDetail = detail,
                LastSupplementMatchCount = matchCount
            };
        }
    }
}

public sealed record AiScoreSourceHealthSnapshot
{
    public string Status { get; init; } = "Idle";
    public DateTime? LastAttemptUtc { get; init; }
    public DateTime? LastSuccessUtc { get; init; }
    public string? LastStage { get; init; }
    public string? LastDetail { get; init; }
    public int LastMatchCount { get; init; }
    public int LastFallbackMatchCount { get; init; }
    public int LastSupplementMatchCount { get; init; }
    public bool IsCoolingDown { get; init; }
    public DateTime? CooldownUntilUtc { get; init; }

    public static AiScoreSourceHealthSnapshot CreateDefault() => new();
}
