using MatchPredictor.Application.Helpers;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Utils;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class ScoreSnapshotKeyFactoryTests
{
    [Fact]
    public void Apply_ProducesBoundedKeysForVeryLongTeamNames()
    {
        var kickoffUtc = DateTimeProvider.ConvertLocalToUtc(DateTimeProvider.GetLocalTime().Date.AddHours(18));
        var longName = new string('A', 2000) + " FC";

        var score = new MatchScore
        {
            League = "Test League",
            HomeTeam = longName,
            AwayTeam = "Opponent",
            Score = "1:0",
            MatchTime = kickoffUtc
        };

        ScoreSnapshotKeyFactory.Apply(score);

        Assert.True(score.HomeTeamKey.Length <= 512);
        Assert.True(score.AwayTeamKey.Length <= 512);
        Assert.True(score.LeagueKey.Length <= 512);
        Assert.Equal(DateTimeProvider.ConvertUtcToLocalDate(kickoffUtc), score.MatchLocalDate);
    }

    [Fact]
    public void Apply_ProducesSameKeysForEquivalentTeamSpellings()
    {
        var kickoffUtc = DateTimeProvider.ConvertLocalToUtc(DateTimeProvider.GetLocalTime().Date.AddHours(20));
        var league = "England - Premier League";

        var canonical = new AiScoreMatchScore
        {
            League = league,
            HomeTeam = "Manchester United",
            AwayTeam = "Liverpool",
            Score = "2:1",
            MatchTime = kickoffUtc
        };

        var alias = new AiScoreMatchScore
        {
            League = league,
            HomeTeam = "Man United",
            AwayTeam = "Liverpool",
            Score = "2:1",
            MatchTime = kickoffUtc
        };

        ScoreSnapshotKeyFactory.Apply(canonical);
        ScoreSnapshotKeyFactory.Apply(alias);

        Assert.Equal(canonical.HomeTeamKey, alias.HomeTeamKey);
        Assert.Equal(canonical.AwayTeamKey, alias.AwayTeamKey);
        Assert.Equal(canonical.LeagueKey, alias.LeagueKey);
        Assert.Equal(canonical.MatchLocalDate, alias.MatchLocalDate);
    }
}
