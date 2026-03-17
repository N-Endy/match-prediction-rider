using MatchPredictor.Infrastructure.Services;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class AiScoreSourceHealthTrackerTests
{
    [Fact]
    public void RecordBrowserBlocked_PutsTrackerIntoCooldown()
    {
        var tracker = new AiScoreSourceHealthTracker();

        tracker.RecordBrowserBlocked("Blocked by challenge page.", TimeSpan.FromMinutes(10));

        var inCooldown = tracker.IsInCooldown(DateTime.UtcNow, out var remaining);
        var snapshot = tracker.GetSnapshot();

        Assert.True(inCooldown);
        Assert.True(remaining > TimeSpan.Zero);
        Assert.True(snapshot.IsCoolingDown);
        Assert.Equal("Cooldown", snapshot.Status);
    }

    [Fact]
    public void RecordSuccess_ClearsCooldownAndStoresHealthyCount()
    {
        var tracker = new AiScoreSourceHealthTracker();
        tracker.RecordBrowserBlocked("Blocked by challenge page.", TimeSpan.FromMinutes(10));

        tracker.RecordSuccess("http", 212, "Recovered.");

        var inCooldown = tracker.IsInCooldown(DateTime.UtcNow, out _);
        var snapshot = tracker.GetSnapshot();

        Assert.False(inCooldown);
        Assert.False(snapshot.IsCoolingDown);
        Assert.Equal("Healthy", snapshot.Status);
        Assert.Equal(212, tracker.GetLastSuccessfulMatchCount());
        Assert.Equal(212, snapshot.LastMatchCount);
    }
}
