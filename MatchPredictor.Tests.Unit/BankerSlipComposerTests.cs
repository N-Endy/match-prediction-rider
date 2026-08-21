using MatchPredictor.Application.Helpers;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class BankerSlipComposerTests
{
    [Fact]
    public void Compose_LandsProductInPrimaryRange()
    {
        var candidates = new List<BetslipComposerCandidate>
        {
            Make(1, "a", 0.90m, 1.55),
            Make(2, "b", 0.88m, 1.60),
            Make(3, "c", 0.86m, 1.70),
            Make(4, "d", 0.84m, 1.45),
            Make(5, "e", 0.82m, 1.50)
        };

        var result = BankerSlipComposer.Compose(candidates, 5.0, 10.0, 4.0, 12.0, maxPicks: 8);

        Assert.False(result.IsEmpty);
        Assert.False(result.UsedFallbackRange);
        Assert.InRange(result.CombinedOdds, 5.0, 10.0);
        Assert.Equal(result.Selections.Count, result.Selections.Select(s => s.FixtureKey).Distinct().Count());
    }

    [Fact]
    public void Compose_WidensWhenPrimaryRangeImpossible()
    {
        // With maxPicks=3, 1.60^3 ≈ 4.10 lands in fallback 4-12 but not primary 5-10.
        var candidates = new List<BetslipComposerCandidate>
        {
            Make(1, "a", 0.90m, 1.60),
            Make(2, "b", 0.88m, 1.60),
            Make(3, "c", 0.86m, 1.60),
            Make(4, "d", 0.84m, 1.60)
        };

        var result = BankerSlipComposer.Compose(candidates, 5.0, 10.0, 4.0, 12.0, maxPicks: 3);

        Assert.False(result.IsEmpty);
        Assert.True(result.UsedFallbackRange);
        Assert.InRange(result.CombinedOdds, 4.0, 12.0);
        Assert.Equal(3, result.Selections.Count);
    }

    [Fact]
    public void Compose_ReturnsEmpty_WhenEvenFallbackImpossible()
    {
        var candidates = new List<BetslipComposerCandidate>
        {
            Make(1, "a", 0.90m, 1.10),
            Make(2, "b", 0.88m, 1.10)
        };

        var result = BankerSlipComposer.Compose(candidates, 5.0, 10.0, 4.0, 12.0, maxPicks: 8);

        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void Compose_NeverUsesTwoPicksFromSameFixture()
    {
        var candidates = new List<BetslipComposerCandidate>
        {
            Make(1, "same", 0.95m, 2.0),
            Make(2, "same", 0.94m, 2.1),
            Make(3, "b", 0.90m, 2.0),
            Make(4, "c", 0.88m, 1.8)
        };

        var result = BankerSlipComposer.Compose(candidates, 5.0, 10.0, 4.0, 12.0, maxPicks: 8);

        Assert.False(result.IsEmpty);
        Assert.Equal(result.Selections.Count, result.Selections.Select(s => s.FixtureKey).Distinct().Count());
        Assert.Equal(1, result.Selections.Count(s => s.FixtureKey == "same"));
    }

    [Fact]
    public void Compose_SkipsCandidatesWithoutOdds()
    {
        var candidates = new List<BetslipComposerCandidate>
        {
            Make(1, "a", 0.99m, null),
            Make(2, "b", 0.90m, 1.80),
            Make(3, "c", 0.88m, 1.80),
            Make(4, "d", 0.86m, 1.80)
        };

        var result = BankerSlipComposer.Compose(candidates, 5.0, 10.0, 4.0, 12.0, maxPicks: 8);

        Assert.DoesNotContain(result.Selections, s => s.PredictionId == 1);
    }

    [Fact]
    public void Compose_FindsBand_WhenShortPricedFavoritesCrowdGreedyPath()
    {
        // Old greedy: take 8×1.15 ≈ 3.06 below the 4.0 floor and give up.
        // Search must prefer the 1.90 legs: 1.90^3 ≈ 6.86 inside 5-10x.
        var candidates = new List<BetslipComposerCandidate>();
        for (var i = 1; i <= 10; i++)
        {
            candidates.Add(Make(i, $"short-{i}", 0.95m - i * 0.001m, 1.15));
        }

        candidates.Add(Make(101, "value-a", 0.80m, 1.90));
        candidates.Add(Make(102, "value-b", 0.79m, 1.90));
        candidates.Add(Make(103, "value-c", 0.78m, 1.90));

        var result = BankerSlipComposer.Compose(candidates, 5.0, 10.0, 4.0, 12.0, maxPicks: 8);

        Assert.False(result.IsEmpty);
        Assert.False(result.UsedFallbackRange);
        Assert.InRange(result.CombinedOdds, 5.0, 10.0);
        Assert.All(result.Selections, s => Assert.StartsWith("value-", s.FixtureKey));
    }

    [Fact]
    public void Compose_RespectsMaxPicks_AndNeverExceedsMaxOdds()
    {
        var candidates = new List<BetslipComposerCandidate>
        {
            Make(1, "a", 0.90m, 3.0),
            Make(2, "b", 0.88m, 3.0),
            Make(3, "c", 0.86m, 3.0),
            Make(4, "d", 0.84m, 3.0)
        };

        var result = BankerSlipComposer.Compose(candidates, 5.0, 10.0, 4.0, 12.0, maxPicks: 2);

        Assert.False(result.IsEmpty);
        Assert.True(result.Selections.Count <= 2);
        Assert.True(result.CombinedOdds <= 10.0);
        Assert.InRange(result.CombinedOdds, 5.0, 10.0);
    }

    [Fact]
    public void Compose_PrefersFewerLegs_ThenHigherConfidence_ThenLowerProduct()
    {
        // 1.90^3 ≈ 6.86 (3 legs) and 1.50^5 ≈ 7.59 (5 legs) both in band; prefer 3 legs.
        var candidates = new List<BetslipComposerCandidate>
        {
            Make(1, "hi-a", 0.91m, 1.90),
            Make(2, "hi-b", 0.90m, 1.90),
            Make(3, "hi-c", 0.89m, 1.90),
            Make(4, "lo-a", 0.80m, 1.50),
            Make(5, "lo-b", 0.79m, 1.50),
            Make(6, "lo-c", 0.78m, 1.50),
            Make(7, "lo-d", 0.77m, 1.50),
            Make(8, "lo-e", 0.76m, 1.50)
        };

        var result = BankerSlipComposer.Compose(candidates, 5.0, 10.0, 4.0, 12.0, maxPicks: 8);

        Assert.False(result.IsEmpty);
        Assert.Equal(3, result.Selections.Count);
        Assert.All(result.Selections, s => Assert.StartsWith("hi-", s.FixtureKey));
    }

    [Fact]
    public void Compose_ReturnsEmpty_ForEmptyAndUnpricedPools()
    {
        Assert.True(BankerSlipComposer.Compose([], 5.0, 10.0, 4.0, 12.0).IsEmpty);

        var unpriced = new List<BetslipComposerCandidate>
        {
            Make(1, "a", 0.90m, null),
            Make(2, "b", 0.88m, null)
        };
        var result = BankerSlipComposer.Compose(unpriced, 5.0, 10.0, 4.0, 12.0);
        Assert.True(result.IsEmpty);
        Assert.Equal(0d, result.BestAchievableProduct);
    }

    [Fact]
    public void Compose_ReportsBestAchievableProduct_WhenStuckBelowFloor()
    {
        var candidates = new List<BetslipComposerCandidate>();
        for (var i = 1; i <= 10; i++)
        {
            candidates.Add(Make(i, $"short-{i}", 0.90m, 1.15));
        }

        var result = BankerSlipComposer.Compose(candidates, 5.0, 10.0, 4.0, 12.0, maxPicks: 8);

        Assert.True(result.IsEmpty);
        Assert.True(result.BestAchievableProduct > 1d);
        Assert.True(result.BestAchievableProduct < 4.0);
    }

    private static BetslipComposerCandidate Make(int id, string fixture, decimal confidence, double? odds) =>
        new()
        {
            PredictionId = id,
            FixtureKey = fixture,
            League = "Test",
            HomeTeam = $"Home{id}",
            AwayTeam = $"Away{id}",
            Market = "BTTS",
            PredictedOutcome = "BTTS",
            PredictionCategory = "BothTeamsScore",
            Confidence = confidence,
            DecimalOdds = odds,
            MatchDateTimeUtc = DateTime.UtcNow.AddHours(id)
        };
}
