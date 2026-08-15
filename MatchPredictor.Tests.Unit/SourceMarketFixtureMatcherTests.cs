using MatchPredictor.Application.Helpers;
using MatchPredictor.Domain.Models;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class SourceMarketFixtureMatcherTests
{
    [Fact]
    public void FindBestFixture_ReturnsDualTeamMatch_WhenCombinedScoreIsBelowFormer155Gate()
    {
        var home = ScoreMatchingHelper.GetTeamMatchResult("Zebras United", "Zebras Athletic");
        var away = ScoreMatchingHelper.GetTeamMatchResult("Lions United", "Lions Athletic");
        Assert.True(home.IsMatch);
        Assert.True(away.IsMatch);
        Assert.True(home.Score + away.Score < 1.55);

        var fixture = new SourceMarketFixture
        {
            EventId = "evt-fuzzy",
            HomeTeam = "Zebras Athletic",
            AwayTeam = "Lions Athletic",
            League = "Other League"
        };

        var matched = SourceMarketFixtureMatcher.FindBestFixture(
            [fixture],
            "Zebras United",
            "Lions United",
            "Test League",
            scheduledUtc: null);

        Assert.NotNull(matched);
        Assert.Equal("evt-fuzzy", matched.EventId);
    }

    [Fact]
    public void FindBestFixture_ReturnsNull_WhenKickoffDeltaExceedsEightHours()
    {
        var kickoff = new DateTime(2026, 8, 15, 15, 0, 0, DateTimeKind.Utc);
        var fixture = new SourceMarketFixture
        {
            EventId = "evt-late",
            HomeTeam = "Exact Home",
            AwayTeam = "Exact Away",
            League = "Test League",
            MatchTimeUtc = kickoff.AddHours(9)
        };

        var matched = SourceMarketFixtureMatcher.FindBestFixture(
            [fixture],
            "Exact Home",
            "Exact Away",
            "Test League",
            kickoff);

        Assert.Null(matched);
    }

    [Fact]
    public void FindBestFixture_PrefersCloserKickoff_WhenBothTeamsMatch()
    {
        var kickoff = new DateTime(2026, 8, 15, 15, 0, 0, DateTimeKind.Utc);
        var far = new SourceMarketFixture
        {
            EventId = "evt-far",
            HomeTeam = "Exact Home",
            AwayTeam = "Exact Away",
            League = "Test League",
            MatchTimeUtc = kickoff.AddHours(2)
        };
        var near = new SourceMarketFixture
        {
            EventId = "evt-near",
            HomeTeam = "Exact Home",
            AwayTeam = "Exact Away",
            League = "Test League",
            MatchTimeUtc = kickoff.AddMinutes(20)
        };

        var matched = SourceMarketFixtureMatcher.FindBestFixture(
            [far, near],
            "Exact Home",
            "Exact Away",
            "Test League",
            kickoff);

        Assert.NotNull(matched);
        Assert.Equal("evt-near", matched.EventId);
    }
}
