using MatchPredictor.Infrastructure.Services;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class SofaScoreSourceHealthTrackerTests
{
    [Fact]
    public void RecordSuccess_TracksCandidateUrlsPagesAndSuccessCount()
    {
        var tracker = new SofaScoreSourceHealthTracker();

        tracker.RecordAttempt("discovery", "fixtures");
        tracker.RecordSuccess("event-page", 3, 7, 5, "Recovered");

        var snapshot = tracker.GetSnapshot();

        Assert.Equal("Healthy", snapshot.Status);
        Assert.Equal(1, snapshot.TotalAttempts);
        Assert.Equal(1, snapshot.TotalSuccesses);
        Assert.Equal(3, snapshot.LastMatchCount);
        Assert.Equal(7, snapshot.LastCandidateUrlCount);
        Assert.Equal(5, snapshot.LastPageFetchCount);
        Assert.Equal(5, snapshot.TotalPageFetches);
    }

    [Fact]
    public void RecordBlocked_IncrementsBlockedCount()
    {
        var tracker = new SofaScoreSourceHealthTracker();

        tracker.RecordAttempt("discovery");
        tracker.RecordBlocked("http", "SofaScore returned 403.");

        var snapshot = tracker.GetSnapshot();

        Assert.Equal("Blocked", snapshot.Status);
        Assert.Equal(1, snapshot.TotalAttempts);
        Assert.Equal(1, snapshot.TotalBlocked);
        Assert.Equal("http", snapshot.LastStage);
    }
}
