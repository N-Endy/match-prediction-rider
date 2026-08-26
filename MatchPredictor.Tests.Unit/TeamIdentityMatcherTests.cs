using MatchPredictor.Domain.Helpers;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class TeamIdentityMatcherTests
{
    [Theory]
    [InlineData("Arsenal", "Arsenal W")]
    [InlineData("Arsenal", "Arsenal Women")]
    [InlineData("Barcelona", "Barcelona B")]
    [InlineData("Barcelona", "Barcelona II")]
    public void GetTeamMatchResult_RejectsWomenAndReserveQualifierMismatches(string senior, string other)
    {
        var result = TeamIdentityMatcher.GetTeamMatchResult(senior, other);
        Assert.False(result.IsMatch);
        Assert.True(result.HasQualifierMismatch);
    }

    [Fact]
    public void GetTeamMatchResult_RejectsWomenLeagueInferenceAgainstMensTeam()
    {
        var result = TeamIdentityMatcher.GetTeamMatchResult(
            "Arsenal",
            "Arsenal",
            "England - Premier League",
            "England - Women's Super League");

        Assert.False(result.IsMatch);
        Assert.True(result.HasQualifierMismatch);
    }

    [Fact]
    public void GetTeamMatchResult_MatchesSameSeniorSide()
    {
        var result = TeamIdentityMatcher.GetTeamMatchResult("Man Utd", "Manchester United");
        Assert.True(result.IsMatch);
        Assert.False(result.HasQualifierMismatch);
    }

    [Fact]
    public void GetTeamMatchResultForAdminHint_StillRejectsWomenMismatch()
    {
        var result = TeamIdentityMatcher.GetTeamMatchResultForAdminHint(
            "Arsenal",
            "Arsenal W",
            "England - Premier League",
            "England - Premier League");

        Assert.False(result.IsMatch);
        Assert.True(result.HasQualifierMismatch);
    }
}
