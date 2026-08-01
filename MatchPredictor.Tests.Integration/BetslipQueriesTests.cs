using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class BetslipQueriesTests
{
    [Fact]
    public async Task GetCurrentSetAsync_ReturnsOnlyCurrentSet_WithSlipsAndSelections()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);

        context.BetslipSets.Add(new BetslipSet
        {
            SlipLocalDate = new DateOnly(2026, 8, 1),
            GeneratedAtUtc = DateTime.UtcNow.AddHours(-6),
            RunLabel = BetslipRunLabels.Morning,
            DayKind = BetslipDayKinds.Weekend,
            IsCurrent = false,
            SlipCount = 1,
            Slips =
            [
                new Betslip
                {
                    SlipNumber = 1,
                    Title = "Old",
                    TierLabel = "Old",
                    BookingStatus = BetslipBookingStatuses.Booked,
                    Selections = [new BetslipSelection { HomeTeam = "A", AwayTeam = "B", Market = "BTTS", PredictedOutcome = "BTTS" }]
                }
            ]
        });

        context.BetslipSets.Add(new BetslipSet
        {
            SlipLocalDate = new DateOnly(2026, 8, 1),
            GeneratedAtUtc = DateTime.UtcNow,
            RunLabel = BetslipRunLabels.Midday,
            DayKind = BetslipDayKinds.Weekend,
            IsCurrent = true,
            SlipCount = 1,
            Slips =
            [
                new Betslip
                {
                    SlipNumber = 1,
                    Title = "Current Mega",
                    TierLabel = "Mega (40-50)",
                    BookingCode = "ABC123",
                    BookingStatus = BetslipBookingStatuses.Booked,
                    Selections =
                    [
                        new BetslipSelection
                        {
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

        await context.SaveChangesAsync();

        var queries = new BetslipQueries(context);
        var current = await queries.GetCurrentSetAsync();

        Assert.NotNull(current);
        Assert.Equal(BetslipRunLabels.Midday, current!.RunLabel);
        Assert.True(current.IsCurrent);
        var slip = Assert.Single(current.Slips);
        Assert.Equal("ABC123", slip.BookingCode);
        Assert.Single(slip.Selections);
    }
}
