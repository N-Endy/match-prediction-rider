using MatchPredictor.Web.Configuration;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class HangfireRecurringJobsTests
{
    [Fact]
    public void RegisteredRecurringJobIds_MatchJobsScheduledByRegister()
    {
        Assert.Equal(12, HangfireRecurringJobs.RegisteredRecurringJobIds.Length);
        Assert.Contains("prediction-prewarm-job", HangfireRecurringJobs.RegisteredRecurringJobIds);
        Assert.Contains("prediction-generation-job", HangfireRecurringJobs.RegisteredRecurringJobIds);
        Assert.Contains("prediction-generation-post-analysis-job", HangfireRecurringJobs.RegisteredRecurringJobIds);
        Assert.Contains("prediction-generation-refresh-job", HangfireRecurringJobs.RegisteredRecurringJobIds);
        Assert.Contains("score-update-job", HangfireRecurringJobs.RegisteredRecurringJobIds);
        Assert.Contains("score-backfill-job", HangfireRecurringJobs.RegisteredRecurringJobIds);
        Assert.Contains("closing-line-snapshot-job", HangfireRecurringJobs.RegisteredRecurringJobIds);
        Assert.Contains("daily-analysis-job", HangfireRecurringJobs.RegisteredRecurringJobIds);
        Assert.Contains("historical-backtest-job", HangfireRecurringJobs.RegisteredRecurringJobIds);
        Assert.Contains("team-alias-seed-job", HangfireRecurringJobs.RegisteredRecurringJobIds);
        Assert.Contains("cleanup-old-predictions", HangfireRecurringJobs.RegisteredRecurringJobIds);
        Assert.Contains("betslip-generation-job", HangfireRecurringJobs.RegisteredRecurringJobIds);
    }

    [Fact]
    public void AllRecurringJobIds_IncludesRegisteredAndLegacyIdsForCleanup()
    {
        foreach (var jobId in HangfireRecurringJobs.RegisteredRecurringJobIds)
        {
            Assert.Contains(jobId, HangfireRecurringJobs.AllRecurringJobIds);
        }

        foreach (var jobId in HangfireRecurringJobs.LegacyRecurringJobIds)
        {
            Assert.Contains(jobId, HangfireRecurringJobs.AllRecurringJobIds);
        }

        Assert.Equal(
            HangfireRecurringJobs.RegisteredRecurringJobIds.Length + HangfireRecurringJobs.LegacyRecurringJobIds.Length,
            HangfireRecurringJobs.AllRecurringJobIds.Length);
    }
}
