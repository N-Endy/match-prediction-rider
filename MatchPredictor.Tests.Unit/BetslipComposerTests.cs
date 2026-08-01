using MatchPredictor.Application.Helpers;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class BetslipComposerTests
{
    [Fact]
    public void Compose_WeekendTiers_RespectsMaxAndOnePickPerFixture()
    {
        var candidates = BuildCandidates(120);
        var slips = BetslipComposer.Compose(candidates, BetslipComposer.WeekendTierPlan(), maxSlipsPerPrediction: 3);

        Assert.Equal(10, slips.Count);
        Assert.All(slips.Take(4), slip => Assert.InRange(slip.Selections.Count, 0, 50));
        Assert.All(slips, slip =>
        {
            var fixtures = slip.Selections.Select(s => s.FixtureKey).ToList();
            Assert.Equal(fixtures.Count, fixtures.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        });
    }

    [Fact]
    public void Compose_NeverUsesSamePredictionInMoreThanMaxSlips()
    {
        var candidates = BuildCandidates(80);
        var slips = BetslipComposer.Compose(candidates, BetslipComposer.WeekendTierPlan(), maxSlipsPerPrediction: 3);

        var usage = slips
            .SelectMany(s => s.Selections)
            .GroupBy(s => s.PredictionId)
            .ToDictionary(g => g.Key, g => g.Count());

        Assert.All(usage.Values, count => Assert.True(count <= 3));
    }

    [Fact]
    public void Compose_RespectsMarketShareCap()
    {
        var candidates = new List<BetslipComposerCandidate>();
        for (var i = 1; i <= 40; i++)
        {
            candidates.Add(MakeCandidate(i, $"fx-{i}", "BothTeamsScore", "BTTS", 0.9m - i * 0.001m));
        }

        for (var i = 41; i <= 60; i++)
        {
            candidates.Add(MakeCandidate(i, $"fx-{i}", "StraightWin", "StraightWin", 0.8m - i * 0.001m));
        }

        var tiers = new[]
        {
            new BetslipTierSpec
            {
                SlipNumber = 1,
                Title = "Test",
                TierLabel = "Test",
                MinSelections = 10,
                MaxSelections = 20
            }
        };

        var slips = BetslipComposer.Compose(candidates, tiers, maxSingleMarketShare: 0.4, overProvisionFactor: 1.0);
        var slip = Assert.Single(slips);
        var bttsCount = slip.Selections.Count(s => s.PredictionCategory == "BothTeamsScore");
        Assert.True(bttsCount <= 8, $"Expected BTTS share <= 40% of 20, got {bttsCount}");
    }

    [Fact]
    public void Compose_ShrinksGracefully_WhenPoolIsThin()
    {
        var candidates = BuildCandidates(8);
        var slips = BetslipComposer.Compose(candidates, BetslipComposer.WeekendTierPlan(), overProvisionFactor: 1.0);

        Assert.True(slips[0].Selections.Count <= 8);
        Assert.False(string.IsNullOrWhiteSpace(slips[0].ShortfallNote));
    }

    [Fact]
    public void WeekdayPlan_HasSingleDailySlip()
    {
        var plan = BetslipComposer.WeekdayTierPlan();
        var tier = Assert.Single(plan);
        Assert.Equal(10, tier.MinSelections);
        Assert.Equal(20, tier.MaxSelections);
    }

    private static List<BetslipComposerCandidate> BuildCandidates(int count)
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
            list.Add(MakeCandidate(i, $"fixture-{i}", category, market, 0.95m - i * 0.001m));
        }

        return list;
    }

    private static BetslipComposerCandidate MakeCandidate(
        int id,
        string fixtureKey,
        string category,
        string market,
        decimal confidence) =>
        new()
        {
            PredictionId = id,
            FixtureKey = fixtureKey,
            League = "Test League",
            HomeTeam = $"Home {id}",
            AwayTeam = $"Away {id}",
            Market = market,
            PredictedOutcome = market,
            PredictionCategory = category,
            Confidence = confidence,
            MatchDateTimeUtc = DateTime.UtcNow.AddHours(id)
        };
}
