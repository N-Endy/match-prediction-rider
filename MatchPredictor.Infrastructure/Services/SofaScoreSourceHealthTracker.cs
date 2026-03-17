namespace MatchPredictor.Infrastructure.Services;

public sealed class SofaScoreSourceHealthTracker
{
    private readonly object _gate = new();
    private SofaScoreSourceHealthSnapshot _snapshot = SofaScoreSourceHealthSnapshot.CreateDefault();

    public SofaScoreSourceHealthSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return _snapshot with { };
        }
    }

    public void RecordAttempt(string stage, string? detail = null)
    {
        var nowUtc = DateTime.UtcNow;
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                TotalAttempts = _snapshot.TotalAttempts + 1,
                LastAttemptUtc = nowUtc,
                LastStage = stage,
                LastDetail = detail ?? _snapshot.LastDetail
            };
        }
    }

    public void RecordSuccess(string stage, int matchCount, int candidateUrlCount, int pageFetchCount, string? detail = null)
    {
        var nowUtc = DateTime.UtcNow;
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                Status = "Healthy",
                LastAttemptUtc = nowUtc,
                LastSuccessUtc = nowUtc,
                LastStage = stage,
                LastDetail = detail ?? $"SofaScore returned {matchCount} match(es).",
                LastMatchCount = matchCount,
                LastCandidateUrlCount = candidateUrlCount,
                LastPageFetchCount = pageFetchCount,
                TotalSuccesses = _snapshot.TotalSuccesses + 1,
                TotalPageFetches = _snapshot.TotalPageFetches + pageFetchCount
            };
        }
    }

    public void RecordBlocked(string stage, string detail)
    {
        var nowUtc = DateTime.UtcNow;
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                Status = "Blocked",
                LastAttemptUtc = nowUtc,
                LastStage = stage,
                LastDetail = detail,
                TotalBlocked = _snapshot.TotalBlocked + 1
            };
        }
    }

    public void RecordEmpty(string stage, string detail, int candidateUrlCount = 0, int pageFetchCount = 0)
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
                LastMatchCount = 0,
                LastCandidateUrlCount = candidateUrlCount,
                LastPageFetchCount = pageFetchCount,
                TotalEmpty = _snapshot.TotalEmpty + 1,
                TotalPageFetches = _snapshot.TotalPageFetches + pageFetchCount
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
                LastDetail = detail,
                TotalFailures = _snapshot.TotalFailures + 1
            };
        }
    }
}

public sealed record SofaScoreSourceHealthSnapshot
{
    public string Status { get; init; } = "Idle";
    public DateTime? LastAttemptUtc { get; init; }
    public DateTime? LastSuccessUtc { get; init; }
    public string? LastStage { get; init; }
    public string? LastDetail { get; init; }
    public int LastMatchCount { get; init; }
    public int LastCandidateUrlCount { get; init; }
    public int LastPageFetchCount { get; init; }
    public int TotalAttempts { get; init; }
    public int TotalSuccesses { get; init; }
    public int TotalBlocked { get; init; }
    public int TotalEmpty { get; init; }
    public int TotalFailures { get; init; }
    public int TotalPageFetches { get; init; }

    public static SofaScoreSourceHealthSnapshot CreateDefault() => new();
}
