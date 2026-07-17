using MatchPredictor.Application.Helpers;
using MatchPredictor.Domain.Helpers;
using MatchPredictor.Domain.Models;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class TeamAliasMatchHelperTests
{
    [Fact]
    public void GetTeamMatchResult_TreatsSharedTeamIdAsExactPair()
    {
        var psgKey = TeamNameNormalizer.NormalizeAlias("PSG");
        var parisKey = TeamNameNormalizer.NormalizeAlias("Paris Saint Germain");
        var lookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [psgKey] = 42,
            [parisKey] = 42,
            [TeamNameNormalizer.BuildAliasKey("PSG", "France Ligue 1")] = 42,
            [TeamNameNormalizer.BuildAliasKey("Paris Saint Germain", "France Ligue 1")] = 42
        };

        var result = TeamAliasMatchHelper.GetTeamMatchResult(
            "PSG",
            "Paris Saint Germain",
            "France Ligue 1",
            "France Ligue 1",
            lookup);

        Assert.True(result.IsMatch);
        Assert.True(result.IsExactKeyMatch);
        Assert.Equal(1.0, result.Score);
    }

    [Fact]
    public void FormatRejectionReason_IncludesAmbiguousMarginDetail()
    {
        var formatted = TeamAliasMatchHelper.FormatRejectionReason(
            FixtureMatchRejectionReason.AmbiguousMargin,
            "best=Madrid vs Athletic Bilbao runnerUp=Atletico Madrid vs Athletic Bilbao");

        Assert.StartsWith("AmbiguousMargin:", formatted);
        Assert.Contains("Madrid", formatted);
    }
}
