using System;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure;
using MatchPredictor.Infrastructure.Utils;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class ScoreSettlementRegularTimeTests
{
    [Fact]
    public void ParseScoreDataHtml_ExtractsParenthesizedRegularTimeScore_OnAetMatches()
    {
        var html = """
            <h4>Premier League</h4>
            <span>20:00</span>
            Arsenal - Chelsea
            <a>2-1 (1-1) aet</a>
            """;

        var scores = WebScraperService.ParseScoreDataHtml(html);
        var match = Assert.Single(scores);

        Assert.Equal("Arsenal", match.HomeTeam);
        Assert.Equal("Chelsea", match.AwayTeam);
        Assert.Equal("2:1", match.Score);
        Assert.Equal("1:1", match.RegularTimeScore);
        Assert.True(match.IsExtraTime);
    }

    [Fact]
    public void ParseScoreDataHtml_LeavesRegularTimeScoreNull_WhenAetLacksSubscore()
    {
        var html = """
            <h4>FA Cup</h4>
            <span>19:45</span>
            Liverpool - Everton
            <a>1-3aet</a>
            """;

        var scores = WebScraperService.ParseScoreDataHtml(html);
        var match = Assert.Single(scores);

        Assert.Equal("Liverpool", match.HomeTeam);
        Assert.Equal("Everton", match.AwayTeam);
        Assert.Equal("1:3", match.Score);
        Assert.Null(match.RegularTimeScore);
        Assert.True(match.IsExtraTime);
    }

    [Fact]
    public void ParseScoreDataHtml_SetsRegularTimeScoreToScore_WhenConcludedOnPenaltiesWithoutExtraTimeGoals()
    {
        var html = """
            <h4>Copa del Rey</h4>
            <span>21:00</span>
            Betis - Sevilla
            <a>1-1 (Pen: 5-6)</a>
            """;

        var scores = WebScraperService.ParseScoreDataHtml(html);
        var match = Assert.Single(scores);

        Assert.Equal("Betis", match.HomeTeam);
        Assert.Equal("Sevilla", match.AwayTeam);
        Assert.Equal("1:1", match.Score);
        Assert.Equal("1:1", match.RegularTimeScore);
        Assert.True(match.IsExtraTime);
    }

    [Fact]
    public void ParseScoreDataHtml_PopulatesRegularTimeScore_ForNormalFullTimeMatches()
    {
        var html = """
            <h4>La Liga</h4>
            <span>17:00</span>
            Real Madrid - Barcelona
            <a>3-1</a>
            """;

        var scores = WebScraperService.ParseScoreDataHtml(html);
        var match = Assert.Single(scores);

        Assert.Equal("Real Madrid", match.HomeTeam);
        Assert.Equal("Barcelona", match.AwayTeam);
        Assert.Equal("3:1", match.Score);
        Assert.Equal("3:1", match.RegularTimeScore);
        Assert.False(match.IsExtraTime);
        Assert.False(match.IsLive);
    }

    [Fact]
    public void ParseScoreMatchTime_DoesNotShiftEveningFixtureToYesterday_DuringDaytime()
    {
        var today = DateOnly.FromDateTime(DateTimeProvider.GetLocalTime());
        // A 22:00 kickoff on today's listing should NOT be shifted to yesterday if parsed during the day
        var parsedUtc = WebScraperService.ParseScoreMatchTime("22:00", isLive: false, today);
        var local = DateTimeProvider.ConvertUtcToLocal(parsedUtc);

        var nowLocal = DateTimeProvider.GetLocalTime();
        if (nowLocal.Hour > 6)
        {
            Assert.Equal(today, DateOnly.FromDateTime(local));
            Assert.Equal(22, local.Hour);
        }
    }
}
