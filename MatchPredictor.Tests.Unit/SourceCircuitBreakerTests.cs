using MatchPredictor.Domain.Sourcing;
using MatchPredictor.Infrastructure.Services;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class SourceCircuitBreakerTests
{
    [Fact]
    public void RecordFailure_OpensCircuitOnceThresholdReached()
    {
        var breaker = new SourceCircuitBreaker(failureThreshold: 3, openDuration: TimeSpan.FromMinutes(5));
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var state = SourceCircuitState.Closed;
        state = breaker.RecordFailure(state, now);
        Assert.False(breaker.IsOpen(state, now));

        state = breaker.RecordFailure(state, now);
        Assert.False(breaker.IsOpen(state, now));

        state = breaker.RecordFailure(state, now);
        Assert.True(breaker.IsOpen(state, now));
        Assert.Equal(3, state.ConsecutiveFailures);
        Assert.Equal(TimeSpan.FromMinutes(5), breaker.RemainingCooldown(state, now));
    }

    [Fact]
    public void IsOpen_BecomesFalseAfterCooldownElapses()
    {
        var breaker = new SourceCircuitBreaker(failureThreshold: 1, openDuration: TimeSpan.FromMinutes(10));
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var state = breaker.RecordFailure(SourceCircuitState.Closed, now);

        Assert.True(breaker.IsOpen(state, now));
        Assert.False(breaker.IsOpen(state, now.AddMinutes(11)));
    }

    [Fact]
    public void RecordSuccess_ClosesCircuit()
    {
        var breaker = new SourceCircuitBreaker(failureThreshold: 2);
        var now = DateTime.UtcNow;

        var state = breaker.RecordFailure(SourceCircuitState.Closed, now);
        state = breaker.RecordFailure(state, now);
        Assert.True(breaker.IsOpen(state, now));

        state = breaker.RecordSuccess();
        Assert.False(breaker.IsOpen(state, now));
        Assert.Equal(0, state.ConsecutiveFailures);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveThreshold()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceCircuitBreaker(failureThreshold: 0));
    }

    [Fact]
    public void FlashScoreTracker_EntersCooldownAfterRepeatedFailuresAndRecovers()
    {
        var tracker = new FlashScoreSourceHealthTracker(
            new SourceCircuitBreaker(failureThreshold: 2, openDuration: TimeSpan.FromMinutes(10)));
        var now = DateTime.UtcNow;

        tracker.RecordFailure("browser", "blocked once");
        Assert.False(tracker.IsInCooldown(now, out _));

        tracker.RecordBlocked("cloudflare challenge");
        Assert.True(tracker.IsInCooldown(now, out var remaining));
        Assert.True(remaining > TimeSpan.Zero);

        var snapshot = tracker.GetHealthSnapshot();
        Assert.Equal("FlashScore", snapshot.SourceName);
        Assert.True(snapshot.IsCoolingDown);
        Assert.Equal(2, snapshot.ConsecutiveFailures);

        tracker.RecordSuccess("browser", matchCount: 12);
        Assert.False(tracker.IsInCooldown(now, out _));
        var healthy = tracker.GetHealthSnapshot();
        Assert.Equal("Healthy", healthy.Status);
        Assert.Equal(0, healthy.ConsecutiveFailures);
        Assert.Equal(12, healthy.LastMatchCount);
    }
}
