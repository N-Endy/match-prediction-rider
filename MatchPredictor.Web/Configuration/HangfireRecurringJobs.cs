using Hangfire;
using MatchPredictor.Domain.Interfaces;

namespace MatchPredictor.Web.Configuration;

internal static class HangfireRecurringJobs
{
    internal static readonly string[] AllRecurringJobIds =
    [
        "daily-prediction-job",
        "prediction-generation-job-noon",
        "prediction-prewarm-job",
        "prediction-generation-job",
        "prediction-generation-post-analysis-job",
        "prediction-generation-refresh-job",
        "score-update-job",
        "score-backfill-job",
        "closing-line-snapshot-job",
        "daily-analysis-job",
        "cleanup-old-predictions"
    ];

    internal static void RemoveAll(IRecurringJobManager recurringJobs)
    {
        foreach (var jobId in AllRecurringJobIds)
        {
            recurringJobs.RemoveIfExists(jobId);
        }
    }

    internal static void Register(IRecurringJobManager recurringJobs)
    {
        var watTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Lagos");

        recurringJobs.AddOrUpdate<IAnalyzerService>(
            "prediction-prewarm-job",
            service => service.ExtractDataAndSyncDatabaseAsync(1),
            "40 23 * * *",
            new RecurringJobOptions { TimeZone = watTimeZone });

        recurringJobs.AddOrUpdate<IAnalyzerService>(
            "prediction-generation-job",
            service => service.ExtractDataAndSyncDatabaseAsync(),
            "35 0 * * *",
            new RecurringJobOptions { TimeZone = watTimeZone });

        recurringJobs.AddOrUpdate<IAnalyzerService>(
            "prediction-generation-post-analysis-job",
            service => service.ExtractDataAndSyncDatabaseAsync(),
            "30 4 * * *",
            new RecurringJobOptions { TimeZone = watTimeZone });

        recurringJobs.AddOrUpdate<IAnalyzerService>(
            "prediction-generation-refresh-job",
            service => service.ExtractDataAndSyncDatabaseAsync(),
            "30 12,16 * * *",
            new RecurringJobOptions { TimeZone = watTimeZone });

        recurringJobs.AddOrUpdate<IAnalyzerService>(
            "score-update-job",
            service => service.RunScoreUpdaterAsync(1, "recent"),
            "*/6 * * * *",
            new RecurringJobOptions { TimeZone = watTimeZone });

        recurringJobs.AddOrUpdate<IAnalyzerService>(
            "score-backfill-job",
            service => service.RunScoreUpdaterAsync(14, "backfill"),
            "17 * * * *",
            new RecurringJobOptions { TimeZone = watTimeZone });

        recurringJobs.AddOrUpdate<IAnalyzerService>(
            "closing-line-snapshot-job",
            service => service.CaptureClosingLineSnapshotsAsync(15),
            "*/5 * * * *",
            new RecurringJobOptions { TimeZone = watTimeZone });

        recurringJobs.AddOrUpdate<IAnalyzerService>(
            "daily-analysis-job",
            service => service.RunDailyAnalysisAsync(),
            "20 0 * * *",
            new RecurringJobOptions { TimeZone = watTimeZone });

        recurringJobs.AddOrUpdate<IAnalyzerService>(
            "cleanup-old-predictions",
            service => service.CleanupOldPredictionsAndMatchDataAsync(),
            "0 1 * * *",
            new RecurringJobOptions { TimeZone = watTimeZone });
    }
}
