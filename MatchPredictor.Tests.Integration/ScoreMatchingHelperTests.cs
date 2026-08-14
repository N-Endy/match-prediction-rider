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

    [Fact]
    public void GetTeamMatchResult_UsesLeagueContextForImplicitWomenAndYouthQualifiers()
    {
        var womenMatch = ScoreMatchingHelper.GetTeamMatchResult(
            "Newcastle Jets",
            "Newcastle Jets Women",
            "Australia - A League Women",
            "Australia W-League");

        var youthMatch = ScoreMatchingHelper.GetTeamMatchResult(
            "Manchester United",
            "Manchester United U21",
            "England - Premier League 2 U21",
            "England - Premier League 2 U21");

        Assert.True(womenMatch.IsMatch);
        Assert.True(youthMatch.IsMatch);
    }

    [Theory]
    [InlineData("Agrobiznes Volochisk", "Ahrobiznes Volochysk", true)]
    [InlineData("Lokomotiv Kiev", "Lokomotyv Kyiv", true)]
    [InlineData("Dynamo Kiev", "Dynamo Kyiv", true)]
    [InlineData("Kiev", "Roma", false)]
    public void TeamsMatch_NormalizesKievKyivAndAgrobiznesSpellings(string left, string right, bool expected)
    {
        Assert.Equal(expected, ScoreMatchingHelper.TeamsMatch(left, right));
        Assert.Equal(expected, ScoreMatchingHelper.TeamsMatch(right, left));
    }

    [Fact]
    public void GetTeamMatchResult_KievVsKyivClearsSettlementAndHintFloors()
    {
        var home = ScoreMatchingHelper.GetTeamMatchResult(
            "Agrobiznes Volochisk",
            "Ahrobiznes Volochysk",
            "UKRAINE - PERSHA LIGA",
            "UKRAINE: Persha Liga");
        var away = ScoreMatchingHelper.GetTeamMatchResult(
            "Lokomotiv Kiev",
            "Lokomotyv Kyiv",
            "UKRAINE - PERSHA LIGA",
            "UKRAINE: Persha Liga");
        var hintAway = ScoreMatchingHelper.GetTeamMatchResultForAdminHint(
            "Lokomotiv Kiev",
            "Lokomotyv Kyiv",
            "UKRAINE - PERSHA LIGA",
            "UKRAINE: Persha Liga");

        Assert.True(home.IsMatch);
        Assert.True(away.IsMatch);
        Assert.True((home.Score + away.Score) / 2.0 >= 0.84);
        Assert.True(hintAway.Score >= 0.62);
    }
}
