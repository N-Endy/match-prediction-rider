using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Repositories;
using MatchPredictor.Domain.Helpers;
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

    [Fact]
    public async Task GetSlipsForDateAsync_ReturnsMorningAndMiddayBanker_IncludingNonCurrent()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var date = new DateOnly(2026, 8, 17);

        context.Predictions.AddRange(
            CreatePrediction(11, "Home Win", "2-0", "Home Win"),
            CreatePrediction(22, "Home Win", "0-1", "Away Win"));

        context.BetslipSets.Add(CreateSet(
            date,
            BetslipRunLabels.Morning,
            isCurrent: false,
            generatedAtUtc: new DateTime(2026, 8, 17, 1, 0, 0, DateTimeKind.Utc),
            slips:
            [
                CreateSlip(BetslipKinds.BankerSlipNumber, "Banker (5-10x)", 11, "Morning Home", "Morning Away"),
                CreateSlip(1, "Small (2-5)", 99, "Ladder Home", "Ladder Away")
            ]));

        context.BetslipSets.Add(CreateSet(
            date,
            BetslipRunLabels.Midday,
            isCurrent: true,
            generatedAtUtc: new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc),
            slips:
            [
                CreateSlip(BetslipKinds.BankerSlipNumber, "Banker (5-10x)", 22, "Midday Home", "Midday Away")
            ]));

        context.BetslipSets.Add(CreateSet(
            new DateOnly(2026, 8, 16),
            BetslipRunLabels.Midday,
            isCurrent: false,
            generatedAtUtc: new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc),
            slips:
            [
                CreateSlip(BetslipKinds.BankerSlipNumber, "Banker (5-10x)", 33, "Yesterday Home", "Yesterday Away")
            ]));

        await context.SaveChangesAsync();

        var queries = new BetslipQueries(context);
        var records = await queries.GetSlipsForDateAsync(BetslipRecordSection.Banker, date);

        Assert.Equal(2, records.Runs.Count);
        Assert.Equal(BetslipRunLabels.Morning, records.Runs[0].RunLabel);
        Assert.False(records.Runs[0].IsCurrent);
        Assert.Equal(BetslipRunLabels.Midday, records.Runs[1].RunLabel);
        var morningBanker = Assert.Single(records.Runs[0].Slips);
        var middayBanker = Assert.Single(records.Runs[1].Slips);
        Assert.Equal("Morning Home", morningBanker.Selections[0].HomeTeam);
        Assert.Equal("Midday Home", middayBanker.Selections[0].HomeTeam);
        Assert.Contains(11, records.PredictionsById.Keys);
        Assert.Contains(22, records.PredictionsById.Keys);
    }

    [Fact]
    public async Task GetSlipsForDateAsync_LadderFilter_DoesNotLeakBankerOrRollover()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);
        var date = new DateOnly(2026, 8, 15);

        context.BetslipSets.Add(CreateSet(
            date,
            BetslipRunLabels.Morning,
            isCurrent: true,
            generatedAtUtc: new DateTime(2026, 8, 15, 1, 0, 0, DateTimeKind.Utc),
            slips:
            [
                CreateSlip(BetslipKinds.RolloverSlipNumber, "Rollover (1.20-1.50x)", 1, "Roll Home", "Roll Away"),
                CreateSlip(BetslipKinds.BankerSlipNumber, "Banker (5-10x)", 2, "Bank Home", "Bank Away"),
                CreateSlip(1, "Small (2-5)", 3, "Small Home", "Small Away"),
                CreateSlip(4, "Mega (40-50)", 4, "Mega Home", "Mega Away"),
                CreateSlip(5, BetslipKinds.DrawsTierLabel, 5, "Draw Home", "Draw Away")
            ]));

        await context.SaveChangesAsync();

        var queries = new BetslipQueries(context);
        var ladder = await queries.GetSlipsForDateAsync(BetslipRecordSection.Ladder, date);
        var draws = await queries.GetSlipsForDateAsync(BetslipRecordSection.AiDraws, date);
        var rollover = await queries.GetSlipsForDateAsync(BetslipRecordSection.Rollover, date);

        var ladderSlips = Assert.Single(ladder.Runs).Slips;
        Assert.Equal(2, ladderSlips.Count);
        Assert.All(ladderSlips, slip => Assert.True(BetslipKinds.IsLadderSlip(slip)));
        var drawSlip = Assert.Single(Assert.Single(draws.Runs).Slips);
        Assert.True(BetslipKinds.IsDrawSlip(drawSlip));
        var rolloverSlip = Assert.Single(Assert.Single(rollover.Runs).Slips);
        Assert.True(BetslipKinds.IsRolloverSlip(rolloverSlip));
    }

    [Fact]
    public async Task GetSlipDatesAsync_ReturnsDistinctDatesInMonthForSection()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        await using var context = new ApplicationDbContext(options);

        context.BetslipSets.Add(CreateSet(
            new DateOnly(2026, 8, 1),
            BetslipRunLabels.Morning,
            isCurrent: false,
            generatedAtUtc: new DateTime(2026, 8, 1, 1, 0, 0, DateTimeKind.Utc),
            slips: [CreateSlip(BetslipKinds.BankerSlipNumber, "Banker (5-10x)", 1, "A", "B")]));
        context.BetslipSets.Add(CreateSet(
            new DateOnly(2026, 8, 1),
            BetslipRunLabels.Midday,
            isCurrent: true,
            generatedAtUtc: new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
            slips: [CreateSlip(BetslipKinds.BankerSlipNumber, "Banker (5-10x)", 2, "C", "D")]));
        context.BetslipSets.Add(CreateSet(
            new DateOnly(2026, 8, 10),
            BetslipRunLabels.Midday,
            isCurrent: false,
            generatedAtUtc: new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc),
            slips: [CreateSlip(1, "Small (2-5)", 3, "E", "F")]));
        context.BetslipSets.Add(CreateSet(
            new DateOnly(2026, 7, 31),
            BetslipRunLabels.Midday,
            isCurrent: false,
            generatedAtUtc: new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc),
            slips: [CreateSlip(BetslipKinds.BankerSlipNumber, "Banker (5-10x)", 4, "G", "H")]));

        await context.SaveChangesAsync();

        var queries = new BetslipQueries(context);
        var bankerDates = await queries.GetSlipDatesAsync(BetslipRecordSection.Banker, 2026, 8);
        var latest = await queries.GetLatestSlipDateAsync(BetslipRecordSection.Banker);

        Assert.Equal([new DateOnly(2026, 8, 1)], bankerDates);
        Assert.Equal(new DateOnly(2026, 8, 1), latest);
    }

    private static BetslipSet CreateSet(
        DateOnly date,
        string runLabel,
        bool isCurrent,
        DateTime generatedAtUtc,
        List<Betslip> slips)
    {
        return new BetslipSet
        {
            SlipLocalDate = date,
            GeneratedAtUtc = generatedAtUtc,
            RunLabel = runLabel,
            DayKind = BetslipDayKinds.Weekend,
            IsCurrent = isCurrent,
            SlipCount = slips.Count,
            Slips = slips
        };
    }

    private static Betslip CreateSlip(int slipNumber, string tierLabel, int predictionId, string home, string away)
    {
        return new Betslip
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
                    PredictionId = predictionId,
                    HomeTeam = home,
                    AwayTeam = away,
                    Market = "StraightWin",
                    PredictedOutcome = "Home Win",
                    WasBooked = true
                }
            ]
        };
    }

    private static Prediction CreatePrediction(int id, string predicted, string actualScore, string actualOutcome)
    {
        return new Prediction
        {
            Id = id,
            Date = "17-08-2026",
            Time = "15:00",
            MatchLocalDate = new DateOnly(2026, 8, 17),
            League = "Test League",
            HomeTeam = "Home",
            AwayTeam = "Away",
            PredictionCategory = "StraightWin",
            PredictedOutcome = predicted,
            ActualScore = actualScore,
            ActualOutcome = actualOutcome,
            WasPublished = true,
            IsCurrentRevision = true,
            PredictionRunId = Guid.NewGuid()
        };
    }
}
