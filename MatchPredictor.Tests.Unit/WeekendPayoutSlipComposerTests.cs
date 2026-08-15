using MatchPredictor.Application.Helpers;
using MatchPredictor.Domain.Models;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class WeekendPayoutSlipComposerTests
{
    [Fact]
    public void BuildWeekendPlan_HasTwoSmallTwoMediumOneBigOneMega()
    {
        var plan = WeekendPayoutSlipComposer.BuildWeekendPlan(new BetslipSettings());

        Assert.Equal(6, plan.Count);
        Assert.Equal(2, plan.Count(b => b.BandKey == "small"));
        Assert.Equal(2, plan.Count(b => b.BandKey == "medium"));
        Assert.Equal(1, plan.Count(b => b.BandKey == "big"));
        Assert.Equal(1, plan.Count(b => b.BandKey == "mega"));
        Assert.Contains(plan, b => b.Title == "Small Acca A");
        Assert.Contains(plan, b => b.Title == "Mega Acca");
        Assert.Equal(18, plan.First(b => b.BandKey == "small").MaxPicks);
        Assert.Equal(45, plan.First(b => b.BandKey == "mega").MaxPicks);
    }

    [Fact]
    public void BuildWeekdayPlan_HasSingleDailyBand()
    {
        var plan = WeekendPayoutSlipComposer.BuildWeekdayPlan(new BetslipSettings());
        var daily = Assert.Single(plan);
        Assert.Equal("daily", daily.BandKey);
        Assert.Equal(30, daily.MinOdds);
        Assert.Equal(100, daily.MaxOdds);
        Assert.Equal(18, daily.MaxPicks);
    }

    [Fact]
    public void Compose_PacksSmallBandWithinOddsRange()
    {
        var candidates = BuildLadder(60, odds: 1.55);
        var settings = new BetslipSettings
        {
            WeekendSmallSlipCount = 1,
            WeekendMediumSlipCount = 0,
            WeekendBigSlipCount = 0,
            WeekendMegaSlipCount = 0
        };

        var slips = WeekendPayoutSlipComposer.Compose(
            candidates,
            WeekendPayoutSlipComposer.BuildWeekendPlan(settings));

        var slip = Assert.Single(slips);
        Assert.True(slip.IsPayoutBand);
        Assert.InRange(slip.TargetCombinedOdds!.Value, 30, 100);
        Assert.True(slip.Selections.Count <= 18);
        Assert.Equal(
            slip.Selections.Count,
            slip.Selections.Select(s => s.FixtureKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Compose_PacksMegaBand_PreferringHigherSingles()
    {
        var candidates = BuildLadder(80, odds: 1.9);
        var settings = new BetslipSettings
        {
            WeekendSmallSlipCount = 0,
            WeekendMediumSlipCount = 0,
            WeekendBigSlipCount = 0,
            WeekendMegaSlipCount = 1
        };

        var slips = WeekendPayoutSlipComposer.Compose(
            candidates,
            WeekendPayoutSlipComposer.BuildWeekendPlan(settings));

        var slip = Assert.Single(slips);
        Assert.True(slip.IsMega);
        Assert.InRange(slip.TargetCombinedOdds!.Value, 5000, 50000);
        Assert.True(slip.Selections.Count <= 45);
    }

    [Fact]
    public void Compose_UsesFallbackRange_WhenPrimaryImpossible()
    {
        // Product of eight 1.25s ≈ 5.96 — below Small primary min (30) but can hit fallback only with many legs if we had them.
        // Instead: give odds that clear fallback 20–120 but carefully...
        // 1.55^8 ≈ 33.4 → hits primary. Need cases where primary fails.
        // Use odds 1.20 with max 8 picks: 1.2^8 ≈ 4.3 — can't hit even fallback 20.
        // Give 40 candidates at 1.35: 1.35^8 ≈ 11.0 still below 20.
        // 1.5^8 ≈ 25.6 → within fallback 20–120, not primary 30–100.
        var candidates = BuildLadder(40, odds: 1.5);
        var settings = new BetslipSettings
        {
            WeekendSmallSlipCount = 1,
            WeekendMediumSlipCount = 0,
            WeekendBigSlipCount = 0,
            WeekendMegaSlipCount = 0,
            SmallMinOdds = 30,
            SmallMaxOdds = 100,
            SmallFallbackMinOdds = 20,
            SmallFallbackMaxOdds = 120,
            SmallMaxPicks = 8
        };

        var slips = WeekendPayoutSlipComposer.Compose(
            candidates,
            WeekendPayoutSlipComposer.BuildWeekendPlan(settings));

        var slip = Assert.Single(slips);
        Assert.InRange(slip.TargetCombinedOdds!.Value, 20, 120);
        Assert.Contains("Widened", slip.ShortfallNote ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compose_OmitsBand_WhenImpossible()
    {
        var candidates = BuildLadder(10, odds: 1.05);
        var settings = new BetslipSettings
        {
            WeekendSmallSlipCount = 1,
            WeekendMediumSlipCount = 0,
            WeekendBigSlipCount = 0,
            WeekendMegaSlipCount = 0
        };

        var slips = WeekendPayoutSlipComposer.Compose(
            candidates,
            WeekendPayoutSlipComposer.BuildWeekendPlan(settings));

        Assert.Empty(slips);
    }

    [Fact]
    public void Compose_DiversifiesPredictionUsageAcrossTwoSmalls()
    {
        var candidates = BuildLadder(100, odds: 1.5);
        var settings = new BetslipSettings
        {
            WeekendSmallSlipCount = 2,
            WeekendMediumSlipCount = 0,
            WeekendBigSlipCount = 0,
            WeekendMegaSlipCount = 0,
            MaxSlipsPerPrediction = 1
        };

        var slips = WeekendPayoutSlipComposer.Compose(
            candidates,
            WeekendPayoutSlipComposer.BuildWeekendPlan(settings),
            maxSlipsPerPrediction: 1);

        Assert.Equal(2, slips.Count);
        var shared = slips[0].Selections.Select(s => s.PredictionId)
            .Intersect(slips[1].Selections.Select(s => s.PredictionId))
            .ToList();
        Assert.Empty(shared);

        var sharedFixtures = slips[0].Selections.Select(s => s.FixtureKey)
            .Intersect(slips[1].Selections.Select(s => s.FixtureKey), StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.Empty(sharedFixtures);
    }

    [Fact]
    public void BetslipSettings_DefaultsMaxSlipsPerPredictionToOne()
    {
        Assert.Equal(1, new BetslipSettings().MaxSlipsPerPrediction);
    }

    [Fact]
    public void BetslipSettings_DefaultsLadderMinimumEdgeToOnePercent()
    {
        Assert.Equal(0.01, new BetslipSettings().LadderMinimumEdge);
    }

    [Fact]
    public void Compose_PrefersResearchScoreOverRawConfidence()
    {
        var researched = new BetslipComposerCandidate
        {
            PredictionId = 1,
            FixtureKey = "fixture-researched",
            League = "Test League",
            HomeTeam = "Research Home",
            AwayTeam = "Research Away",
            Market = "BTTS",
            PredictedOutcome = "BTTS",
            PredictionCategory = "BothTeamsScore",
            Confidence = 0.50m,
            MatchDateTimeUtc = DateTime.UtcNow.AddHours(2),
            DecimalOdds = 1.80,
            ResearchScore = 50
        };
        var highConfidence = new BetslipComposerCandidate
        {
            PredictionId = 2,
            FixtureKey = "fixture-confidence",
            League = "Test League",
            HomeTeam = "Confidence Home",
            AwayTeam = "Confidence Away",
            Market = "Over2.5",
            PredictedOutcome = "Over 2.5",
            PredictionCategory = "Over2.5Goals",
            Confidence = 0.99m,
            MatchDateTimeUtc = DateTime.UtcNow.AddHours(3),
            DecimalOdds = 1.80
        };

        var settings = new BetslipSettings
        {
            WeekendSmallSlipCount = 1,
            WeekendMediumSlipCount = 0,
            WeekendBigSlipCount = 0,
            WeekendMegaSlipCount = 0,
            SmallMinOdds = 1.5,
            SmallMaxOdds = 2.0,
            SmallFallbackMinOdds = 1.5,
            SmallFallbackMaxOdds = 2.0,
            SmallMaxPicks = 1
        };

        var slips = WeekendPayoutSlipComposer.Compose(
            [researched, highConfidence],
            WeekendPayoutSlipComposer.BuildWeekendPlan(settings));

        var slip = Assert.Single(slips);
        Assert.Equal(1, Assert.Single(slip.Selections).PredictionId);
    }

    private static List<BetslipComposerCandidate> BuildLadder(int count, double odds)
    {
        var categories = new[]
        {
            ("StraightWin", "StraightWin"),
            ("BothTeamsScore", "BTTS"),
            ("Over2.5Goals", "Over2.5"),
            ("Under2.5Goals", "Under2.5")
        };

        var list = new List<BetslipComposerCandidate>(count);
        for (var i = 1; i <= count; i++)
        {
            var (category, market) = categories[i % categories.Length];
            list.Add(new BetslipComposerCandidate
            {
                PredictionId = i,
                FixtureKey = $"fixture-{i}",
                League = "Test League",
                HomeTeam = $"Home {i}",
                AwayTeam = $"Away {i}",
                Market = market,
                PredictedOutcome = market,
                PredictionCategory = category,
                Confidence = 0.95m - i * 0.001m,
                MatchDateTimeUtc = DateTime.UtcNow.AddHours(i),
                DecimalOdds = odds + (i % 7) * 0.01
            });
        }

        return list;
    }
}
