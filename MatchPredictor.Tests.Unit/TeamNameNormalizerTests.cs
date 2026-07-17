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

    [Fact]
    public void BoundIndexedValue_TruncatesValuesAbovePostgresBtreeSafeLimit()
    {
        var oversized = new string('a', TeamNameNormalizer.MaxIndexedValueLength + 50);

        var bounded = TeamNameNormalizer.BoundIndexedValue(oversized);

        Assert.Equal(TeamNameNormalizer.MaxIndexedValueLength, bounded.Length);
    }

    [Fact]
    public void IsUsableAliasCandidate_RejectsOversizedOrHtmlLikeValues()
    {
        Assert.True(TeamNameNormalizer.IsUsableAliasCandidate("PSG", "France Ligue 1"));
        Assert.False(TeamNameNormalizer.IsUsableAliasCandidate(new string('x', 250), "League"));
        Assert.False(TeamNameNormalizer.IsUsableAliasCandidate("Team", new string('y', 400)));
        Assert.False(TeamNameNormalizer.IsUsableAliasCandidate("<div>Team</div>", "League"));
        Assert.False(TeamNameNormalizer.IsUsableAliasCandidate("Team", "{\"league\":1}"));
    }
}
