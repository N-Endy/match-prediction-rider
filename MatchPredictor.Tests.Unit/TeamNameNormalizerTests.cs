using MatchPredictor.Domain.Helpers;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class TeamNameNormalizerTests
{
    [Theory]
    [InlineData("Man Utd", "manchester united")]
    [InlineData("PSG Fem", "paris saint germain women")]
    [InlineData("Real B", "real reserve")]
    public void NormalizeAlias_ExpandsCommonFootballSynonyms(string input, string expected)
    {
        Assert.Equal(expected, TeamNameNormalizer.NormalizeAlias(input));
    }

    [Fact]
    public void BuildAliasKey_IncludesNormalizedLeagueScope_WhenProvided()
    {
        var key = TeamNameNormalizer.BuildAliasKey("Man Utd", "Premier League");

        Assert.Equal("premier league|manchester united", key);
    }

    [Fact]
    public void BuildAliasKey_ReturnsEmpty_ForMissingTeamName()
    {
        Assert.Equal(string.Empty, TeamNameNormalizer.BuildAliasKey("", "League"));
    }
}
