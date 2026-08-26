using Hangfire;
using Hangfire.PostgreSql;
using Hangfire.Storage;
using MatchPredictor.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace MatchPredictor.Web.Configuration;

internal static class HangfireRecurringJobs
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    internal static readonly string[] RegisteredRecurringJobIds =
    [
        "prediction-prewarm-job",
        "prediction-generation-job",
        "prediction-generation-post-analysis-job",
        "prediction-generation-refresh-job",
        "score-update-job",
        "score-backfill-job",
        "closing-line-snapshot-job",
        "daily-analysis-job",
        "historical-backtest-job",
        "team-alias-seed-job",
        "team-match-stats-sync-job",
        "statistical-coverage-diagnostics-job",
        "cleanup-old-predictions",
        "betslip-generation-job"
    ];

    /// <summary>
    /// Retired job ids still cleared on startup so orphaned Hangfire rows do not keep firing.
    /// </summary>
    internal static readonly string[] LegacyRecurringJobIds =
    [
        "daily-prediction-job",
        "prediction-generation-job-noon"
    ];

    internal static readonly string[] AllRecurringJobIds =
        [.. RegisteredRecurringJobIds, .. LegacyRecurringJobIds];

    /// <summary>
    /// Clears every known recurring job (used when switching to external cron).
    /// Retries around Hangfire Postgres lock contention during overlapping deploys.
    /// </summary>
    internal static void RemoveAll(IRecurringJobManager recurringJobs, ILogger logger)
    {
        foreach (var jobId in AllRecurringJobIds)
        {
            TryWithLockRetry(logger, $"remove:{jobId}", () => recurringJobs.RemoveIfExists(jobId));
        }
    }

    /// <summary>
    /// Removes only retired job ids. Active jobs are upserted via <see cref="Register"/> —
    /// deleting them first races the still-running previous instance for distributed locks.
    /// </summary>
    internal static void RemoveLegacy(IRecurringJobManager recurringJobs, ILogger logger)
    {
        foreach (var jobId in LegacyRecurringJobIds)
        {
            TryWithLockRetry(logger, $"remove-legacy:{jobId}", () => recurringJobs.RemoveIfExists(jobId));
        }
    }

    internal static void Register(IRecurringJobManager recurringJobs, ILogger logger)
    {
        var watTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Lagos");

        TryWithLockRetry(logger, "register:prediction-prewarm-job", () =>
            recurringJobs.AddOrUpdate<IAnalyzerService>(
                "prediction-prewarm-job",
                service => service.ExtractDataAndSyncDatabaseAsync(1),
                "40 23 * * *",
                new RecurringJobOptions { TimeZone = watTimeZone }));

        TryWithLockRetry(logger, "register:prediction-generation-job", () =>
            recurringJobs.AddOrUpdate<IAnalyzerService>(
                "prediction-generation-job",
                service => service.ExtractDataAndSyncDatabaseAsync(),
                "35 0 * * *",
                new RecurringJobOptions { TimeZone = watTimeZone }));

        TryWithLockRetry(logger, "register:prediction-generation-post-analysis-job", () =>
            recurringJobs.AddOrUpdate<IAnalyzerService>(
                "prediction-generation-post-analysis-job",
                service => service.ExtractDataAndSyncDatabaseAsync(),
                "30 4 * * *",
                new RecurringJobOptions { TimeZone = watTimeZone }));

        TryWithLockRetry(logger, "register:prediction-generation-refresh-job", () =>
            recurringJobs.AddOrUpdate<IAnalyzerService>(
                "prediction-generation-refresh-job",
                service => service.ExtractDataAndSyncDatabaseAsync(),
                "30 12,16 * * *",
                new RecurringJobOptions { TimeZone = watTimeZone }));

        TryWithLockRetry(logger, "register:score-update-job", () =>
            recurringJobs.AddOrUpdate<IAnalyzerService>(
                "score-update-job",
                service => service.RunScoreUpdaterAsync(1, "recent"),
                OperationalSchedule.ScoreUpdateCron,
                new RecurringJobOptions { TimeZone = watTimeZone }));

        TryWithLockRetry(logger, "register:score-backfill-job", () =>
            recurringJobs.AddOrUpdate<IAnalyzerService>(
                "score-backfill-job",
                service => service.RunScoreUpdaterAsync(14, "backfill"),
                "17 * * * *",
                new RecurringJobOptions { TimeZone = watTimeZone }));

        TryWithLockRetry(logger, "register:closing-line-snapshot-job", () =>
            recurringJobs.AddOrUpdate<IAnalyzerService>(
                "closing-line-snapshot-job",
                service => service.CaptureClosingLineSnapshotsAsync(15),
                "*/5 * * * *",
                new RecurringJobOptions { TimeZone = watTimeZone }));

        TryWithLockRetry(logger, "register:daily-analysis-job", () =>
            recurringJobs.AddOrUpdate<IAnalyzerService>(
                "daily-analysis-job",
                service => service.RunDailyAnalysisAsync(),
                "20 0 * * *",
                new RecurringJobOptions { TimeZone = watTimeZone }));

        TryWithLockRetry(logger, "register:historical-backtest-job", () =>
            recurringJobs.AddOrUpdate<IHistoricalBacktestService>(
                "historical-backtest-job",
                service => service.RunNightlyBacktestAsync(CancellationToken.None),
                "25 0 * * *",
                new RecurringJobOptions { TimeZone = watTimeZone }));

        TryWithLockRetry(logger, "register:team-alias-seed-job", () =>
            recurringJobs.AddOrUpdate<ITeamResolutionService>(
                "team-alias-seed-job",
                service => service.SeedAliasesFromExistingDataAsync(CancellationToken.None),
                "5 1 * * *",
                new RecurringJobOptions { TimeZone = watTimeZone }));

        TryWithLockRetry(logger, "register:team-match-stats-sync-job", () =>
            recurringJobs.AddOrUpdate<ITeamMatchStatsSyncService>(
                "team-match-stats-sync-job",
                service => service.SyncFromMatchScoresAsync(600, CancellationToken.None),
                "15 1 * * *",
                new RecurringJobOptions { TimeZone = watTimeZone }));

        TryWithLockRetry(logger, "register:statistical-coverage-diagnostics-job", () =>
            recurringJobs.AddOrUpdate<IStatisticalCoverageDiagnostics>(
                "statistical-coverage-diagnostics-job",
                service => service.RunNightlyDiagnosticsAsync(CancellationToken.None),
                "30 1 * * *",
                new RecurringJobOptions { TimeZone = watTimeZone }));

        TryWithLockRetry(logger, "register:cleanup-old-predictions", () =>
            recurringJobs.AddOrUpdate<IAnalyzerService>(
                "cleanup-old-predictions",
                service => service.CleanupOldPredictionsAndMatchDataAsync(),
                "0 1 * * *",
                new RecurringJobOptions { TimeZone = watTimeZone }));

        TryWithLockRetry(logger, "register:betslip-generation-job", () =>
            recurringJobs.AddOrUpdate<IBetslipGenerationService>(
                "betslip-generation-job",
                service => service.GenerateDailyBetslipsAsync(null),
                "0 2,13 * * *",
                new RecurringJobOptions { TimeZone = watTimeZone }));
    }

    private static void TryWithLockRetry(ILogger logger, string operation, Action action)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (IsDistributedLockTimeout(ex))
            {
                if (attempt == MaxAttempts)
                {
                    logger.LogWarning(
                        ex,
                        "Hangfire lock timed out for {Operation} after {Attempts} attempts. Leaving existing recurring-job state in place so startup can continue.",
                        operation,
                        MaxAttempts);
                    return;
                }

                logger.LogWarning(
                    "Hangfire lock contention on {Operation} (attempt {Attempt}/{MaxAttempts}). Retrying in {DelaySeconds}s.",
                    operation,
                    attempt,
                    MaxAttempts,
                    RetryDelay.TotalSeconds);
                Thread.Sleep(RetryDelay);
            }
        }
    }

    private static bool IsDistributedLockTimeout(Exception ex) =>
        ex is PostgreSqlDistributedLockException or DistributedLockTimeoutException
        || ex.InnerException is PostgreSqlDistributedLockException or DistributedLockTimeoutException
        || (ex.Message?.Contains("distributed lock", StringComparison.OrdinalIgnoreCase) ?? false);
}
