using MatchPredictor.Application.Helpers;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class ScoreMatchingHelperTests
{
    [Theory]
    [InlineData("Manchester City", "Leicester City", false)]
    [InlineData("Real Madrid", "Real Betis", false)]
    [InlineData("Slavia Prague", "Slavia Prague B", false)]
    [InlineData("Sheff Utd", "Sheffield United", true)]
    [InlineData("Inter Bogota W", "Inter Bogota Women", true)]
    [InlineData("Vasalund (Swe)", "Vasalund", true)]
    public void TeamsMatch_UsesPrecisionFirstRules(string left, string right, bool expected)
    {
        Assert.Equal(expected, ScoreMatchingHelper.TeamsMatch(left, right));
        Assert.Equal(expected, ScoreMatchingHelper.TeamsMatch(right, left));
    }

    [Fact]
    public void CreateTeamLookupKey_NormalizesAnnotationsAndEquivalentQualifiers()
    {
        var left = ScoreMatchingHelper.CreateTeamLookupKey("Slavia Prague B (Cze)");
        var right = ScoreMatchingHelper.CreateTeamLookupKey("Slavia Prague II");

        Assert.Equal(left, right);
    }
}
