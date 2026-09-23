using MatchPredictor.Domain.Helpers;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Repositories;
using MatchPredictor.Infrastructure.Utils;
using MatchPredictor.Web.Pages;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class BetslipsRecordsDateTests
{
    [Fact]
    public async Task OnGetAsync_SelectsLatestDateForSection_WhenNoDateIsProvided()
    {
        var today = DateTimeProvider.GetLocalDate();
        var threeDaysAgo = today.AddDays(-3);
        await using var context = CreateContext();
        SeedSection(context, today, BetslipKinds.RolloverSlipNumber, "Rollover (1.30-1.50x)");
        SeedSection(context, threeDaysAgo, 1, "Small (2-5)");
        await context.SaveChangesAsync();

        var page = CreatePage(context);
        await page.OnGetAsync("ladder", date: null, month: null, CancellationToken.None);

        Assert.Equal(BetslipRecordSection.Ladder, page.RecordSection);
        Assert.Equal(threeDaysAgo, page.RecordDate);
        Assert.True(page.HasRecordDateSelection);
    }

    [Fact]
    public async Task OnGetAsync_FallsBackToSectionLatest_WhenInheritedDateHasNoSlips()
    {
        var today = DateTimeProvider.GetLocalDate();
        var threeDaysAgo = today.AddDays(-3);
        await using var context = CreateContext();
        SeedSection(context, today, BetslipKinds.RolloverSlipNumber, "Rollover (1.30-1.50x)");
        SeedSection(context, threeDaysAgo, 1, "Small (2-5)");
        await context.SaveChangesAsync();

        var page = CreatePage(context);
        await page.OnGetAsync("rollover", date: threeDaysAgo, month: null, CancellationToken.None);

        Assert.Equal(BetslipRecordSection.Rollover, page.RecordSection);
        Assert.Equal(today, page.RecordDate);
        Assert.True(page.HasRecordDateSelection);
        Assert.NotEmpty(page.Results.Runs);
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new ApplicationDbContext(options);
    }

    private static BetslipsModel CreatePage(ApplicationDbContext context)
    {
        var page = new BetslipsModel(
            new BetslipQueries(context),
            Options.Create(new BetslipSettings()));
        page.PageContext = new PageContext
        {
            HttpContext = new DefaultHttpContext(),
            ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        };
        return page;
    }

    private static void SeedSection(
        ApplicationDbContext context,
        DateOnly date,
        int slipNumber,
        string tierLabel)
    {
        context.BetslipSets.Add(new BetslipSet
        {
            SlipLocalDate = date,
            GeneratedAtUtc = DateTime.UtcNow.AddDays((date.DayNumber - DateTimeProvider.GetLocalDate().DayNumber)),
            RunLabel = BetslipRunLabels.Morning,
            DayKind = BetslipDayKinds.Weekday,
            IsCurrent = date == DateTimeProvider.GetLocalDate(),
            SlipCount = 1,
            Slips =
            [
                new Betslip
                {
                    SlipNumber = slipNumber,
                    Title = tierLabel,
                    TierLabel = tierLabel,
                    BookingStatus = BetslipBookingStatuses.Booked,
                    BookingCode = "CODE",
                    SelectionCount = 1,
                    Selections =
                    [
                        new BetslipSelection
                        {
                            PredictionId = slipNumber + date.DayNumber,
                            HomeTeam = "Home",
                            AwayTeam = "Away",
                            Market = "Over2.5",
                            PredictedOutcome = "Over 2.5",
                            WasBooked = true
                        }
                    ]
                }
            ]
        });
    }
}
