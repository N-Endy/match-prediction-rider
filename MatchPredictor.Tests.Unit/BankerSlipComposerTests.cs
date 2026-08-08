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
