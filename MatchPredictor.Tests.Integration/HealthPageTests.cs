using System.Text.Json;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Services;
using MatchPredictor.Infrastructure.Utils;
using MatchPredictor.Web.Pages.Health;
using MatchPredictor.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class HealthPageTests
{
    [Fact]
    public async Task OnGetAsync_BuildsOperationalSnapshotFromSignals()
    {
        await using var context = CreateContext();
        var today = DateTimeProvider.GetLocalTime().ToString("dd-MM-yyyy");
        context.Predictions.Add(new Prediction
        {
            Date = today,
            Time = "08:00 AM",
            League = "League",
            HomeTeam = "Home",
            AwayTeam = "Away",
            PredictionCategory = "BothTeamsScore",
            PredictedOutcome = "BTTS"
        });

        context.ScrapingLogs.AddRange(
            new ScrapingLog { EventName = "data_sync", Status = "Success", Timestamp = DateTime.UtcNow.AddMinutes(-10), Message = "sync ok" },
            new ScrapingLog { EventName = "prediction_generation", Status = "Success", Timestamp = DateTime.UtcNow.AddMinutes(-9), Message = "gen ok" },
            new ScrapingLog { EventName = "score_update_recent", Status = "Success", Timestamp = DateTime.UtcNow.AddMinutes(-2), Message = "scores ok" },
            new ScrapingLog { EventName = "score_update_backfill", Status = "Success", Timestamp = DateTime.UtcNow.AddMinutes(-30), Message = "backfill ok" },
            new ScrapingLog { EventName = "daily_analysis", Status = "Success", Timestamp = DateTime.UtcNow.AddHours(-8), Message = "analysis ok" });
        await context.SaveChangesAsync();

        var startupState = new OperationalStartupState();
        startupState.MarkDatabaseInitialized();
        startupState.MarkHangfireInitialized();
        startupState.MarkRecurringJobsRegistered();

        var aiScoreTracker = new AiScoreSourceHealthTracker();
        var sofaScoreTracker = new SofaScoreSourceHealthTracker();
        sofaScoreTracker.RecordAttempt("discovery", "fixture batch");
        sofaScoreTracker.RecordSuccess("event-page", 2, 4, 3, "SofaScore parsed two pages.");

        var page = new Health(new HealthQueryService(context), startupState, aiScoreTracker, sofaScoreTracker);

        var result = await page.OnGetAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.True(page.Snapshot.Signals.Count >= 5);
        Assert.True(page.Snapshot.DatabaseInitialized);
        Assert.True(page.Snapshot.HangfireInitialized);
        Assert.True(page.Snapshot.RecurringJobsRegistered);
        Assert.True(page.Snapshot.PredictionsToday >= 0);
        Assert.Equal("Healthy", page.Snapshot.SofaScoreRuntime.Status);
        Assert.Equal(2, page.Snapshot.SofaScoreRuntime.LastMatchCount);
        Assert.Equal(1.0, page.Snapshot.SofaScoreRuntime.SuccessRate, 3);
    }

    [Fact]
    public async Task OnGetAsync_UsesPersistedSourceRuntimeSnapshots_WhenLocalTrackersAreIdle()
    {
        await using var context = CreateContext();
        var nowUtc = DateTime.UtcNow;

        context.ScrapingLogs.AddRange(
            new ScrapingLog
            {
                EventName = "data_sync",
                Status = "Success",
                Timestamp = nowUtc.AddMinutes(-10),
                Message = "sync ok"
            },
            new ScrapingLog
            {
                EventName = "prediction_generation",
                Status = "Success",
                Timestamp = nowUtc.AddMinutes(-9),
                Message = "gen ok"
            },
            new ScrapingLog
            {
                EventName = "score_update_recent",
                Status = "Success",
                Timestamp = nowUtc.AddMinutes(-2),
                Message = "scores ok"
            },
            new ScrapingLog
            {
                EventName = "source_runtime_aiscore",
                Status = "Healthy",
                Timestamp = nowUtc.AddMinutes(-1),
                Message = JsonSerializer.Serialize(new AiScoreSourceHealthSnapshot
                {
                    Status = "Healthy",
                    LastStage = "http",
                    LastDetail = "Fetched 18 match(es) from AiScore.",
                    LastAttemptUtc = nowUtc.AddMinutes(-1),
                    LastSuccessUtc = nowUtc.AddMinutes(-1),
                    LastMatchCount = 18
                })
            },
            new ScrapingLog
            {
                EventName = "source_runtime_sofascore",
                Status = "Healthy",
                Timestamp = nowUtc.AddMinutes(-1),
                Message = JsonSerializer.Serialize(new SofaScoreSourceHealthSnapshot
                {
                    Status = "Healthy",
                    LastStage = "event-page",
                    LastDetail = "SofaScore returned 3 match(es).",
                    LastAttemptUtc = nowUtc.AddMinutes(-1),
                    LastSuccessUtc = nowUtc.AddMinutes(-1),
                    LastMatchCount = 3,
                    LastCandidateUrlCount = 5,
                    LastPageFetchCount = 4,
                    TotalAttempts = 1,
                    TotalSuccesses = 1,
                    TotalPageFetches = 4
                })
            });
        await context.SaveChangesAsync();

        var startupState = new OperationalStartupState();
        startupState.MarkDatabaseInitialized();
        startupState.MarkHangfireInitialized();
        startupState.MarkRecurringJobsRegistered();

        var page = new Health(
            new HealthQueryService(context),
            startupState,
            new AiScoreSourceHealthTracker(),
            new SofaScoreSourceHealthTracker());

        var result = await page.OnGetAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal("Healthy", page.Snapshot.AiScoreRuntime.Status);
        Assert.Equal(18, page.Snapshot.AiScoreRuntime.LastMatchCount);
        Assert.Equal("http", page.Snapshot.AiScoreRuntime.LastStage);
        Assert.Equal("Healthy", page.Snapshot.SofaScoreRuntime.Status);
        Assert.Equal(3, page.Snapshot.SofaScoreRuntime.LastMatchCount);
        Assert.Equal(1.0, page.Snapshot.SofaScoreRuntime.SuccessRate, 3);
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options);
    }
}
