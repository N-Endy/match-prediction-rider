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

        var page = new Health(context, startupState, new AiScoreSourceHealthTracker());

        var result = await page.OnGetAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.True(page.Snapshot.Signals.Count >= 5);
        Assert.True(page.Snapshot.DatabaseInitialized);
        Assert.True(page.Snapshot.HangfireInitialized);
        Assert.True(page.Snapshot.RecurringJobsRegistered);
        Assert.True(page.Snapshot.PredictionsToday >= 0);
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options);
    }
}
