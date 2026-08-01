using Hangfire;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Web.Filters;
using MatchPredictor.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace MatchPredictor.Web.Api;

[ApiController]
[Route("api/ops/jobs")]
[CronJobAuth]
public class OpsJobsController : ControllerBase
{
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly OperationalStartupState _startupState;

    public OpsJobsController(IBackgroundJobClient backgroundJobs, OperationalStartupState startupState)
    {
        _backgroundJobs = backgroundJobs;
        _startupState = startupState;
    }

    [HttpPost("{jobName}")]
    public IActionResult Trigger(string jobName)
    {
        if (!_startupState.BackgroundJobsEnabled)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "Background jobs are disabled on this service. Point cron-job.org at the worker URL."
            });
        }

        var normalizedJobName = jobName.Trim().ToLowerInvariant();
        string? hangfireJobId = normalizedJobName switch
        {
            "prediction-prewarm" => _backgroundJobs.Enqueue<IAnalyzerService>(
                service => service.ExtractDataAndSyncDatabaseAsync(1)),
            "prediction-generation" => _backgroundJobs.Enqueue<IAnalyzerService>(
                service => service.ExtractDataAndSyncDatabaseAsync()),
            "prediction-generation-post-analysis" => _backgroundJobs.Enqueue<IAnalyzerService>(
                service => service.ExtractDataAndSyncDatabaseAsync()),
            "prediction-generation-refresh" => _backgroundJobs.Enqueue<IAnalyzerService>(
                service => service.ExtractDataAndSyncDatabaseAsync()),
            "score-update" => _backgroundJobs.Enqueue<IAnalyzerService>(
                service => service.RunScoreUpdaterAsync(1, "recent")),
            "score-backfill" => _backgroundJobs.Enqueue<IAnalyzerService>(
                service => service.RunScoreUpdaterAsync(14, "backfill")),
            "closing-line-snapshot" => _backgroundJobs.Enqueue<IAnalyzerService>(
                service => service.CaptureClosingLineSnapshotsAsync(15)),
            "daily-analysis" => _backgroundJobs.Enqueue<IAnalyzerService>(
                service => service.RunDailyAnalysisAsync()),
            "cleanup-old-predictions" => _backgroundJobs.Enqueue<IAnalyzerService>(
                service => service.CleanupOldPredictionsAndMatchDataAsync()),
            "betslip-generation" => _backgroundJobs.Enqueue<IBetslipGenerationService>(
                service => service.GenerateDailyBetslipsAsync(null)),
            "startup-catchup" => EnqueueStartupCatchup(),
            _ => null
        };

        if (hangfireJobId is null)
        {
            return NotFound(new { error = $"Unknown job: {jobName}" });
        }

        return Accepted(new { jobId = hangfireJobId, jobName = normalizedJobName });
    }

    private string EnqueueStartupCatchup()
    {
        var analysisJobId = _backgroundJobs.Enqueue<IAnalyzerService>(service => service.RunDailyAnalysisAsync());
        _backgroundJobs.ContinueJobWith<IAnalyzerService>(
            analysisJobId,
            service => service.ExtractDataAndSyncDatabaseAsync(),
            JobContinuationOptions.OnlyOnSucceededState);
        return analysisJobId;
    }
}
