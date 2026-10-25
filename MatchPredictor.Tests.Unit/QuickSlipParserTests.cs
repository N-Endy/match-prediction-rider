using MatchPredictor.Domain.Models;
using MatchPredictor.Domain.Services;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class QuickSlipParserTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Parse_EmptyOrWhitespace_ReturnsAmbiguous(string? input)
    {
        var intent = QuickSlipParser.Parse(input!);

        Assert.True(intent.IsAmbiguous);
        Assert.NotEmpty(intent.ClarificationQuestions);
    }

    [Fact]
    public void Parse_EplHomeWins_ResolvesMarketAndLeagueAndCount()
    {
        var intent = QuickSlipParser.Parse("3 safe EPL home wins with odds > 1.5");

        Assert.False(intent.IsAmbiguous);
        Assert.Equal(3, intent.RequestedCount);
        Assert.Contains(PredictionMarket.StraightWin, intent.RequestedMarkets);
        Assert.Equal("Premier League", intent.LeagueHint);
        Assert.Equal("Safe", intent.Bias);
        Assert.Equal(1.5m, intent.MinOdds);
    }

    [Fact]
    public void Parse_BttsAndGgAliases_ResolvesBothTeamsScore()
    {
        var intent = QuickSlipParser.Parse("4 GG matches tonight");

        Assert.Equal(4, intent.RequestedCount);
        Assert.Contains(PredictionMarket.BothTeamsScore, intent.RequestedMarkets);
    }

    [Fact]
    public void Parse_OverAndUnderGoals_ResolvesProperMarkets()
    {
        var overIntent = QuickSlipParser.Parse("treble over 2.5 goals");
        Assert.Equal(3, overIntent.RequestedCount);
        Assert.Contains(PredictionMarket.Over25Goals, overIntent.RequestedMarkets);

        var underIntent = QuickSlipParser.Parse("double under 2.5");
        Assert.Equal(2, underIntent.RequestedCount);
        Assert.Contains(PredictionMarket.Under25Goals, underIntent.RequestedMarkets);
    }

    [Fact]
    public void Parse_OddsRangeBetween_ExtractsMinAndMax()
    {
        var intent = QuickSlipParser.Parse("3 value bets between 1.70 and 2.30 odds");

        Assert.Equal(3, intent.RequestedCount);
        Assert.Equal("Value", intent.Bias);
        Assert.Equal(1.70m, intent.MinOdds);
        Assert.Equal(2.30m, intent.MaxOdds);
    }

    [Fact]
    public void Parse_TeamMention_ExtractsTeamName()
    {
        var intent = QuickSlipParser.Parse("Arsenal over 2.5 goals");

        Assert.Contains(PredictionMarket.Over25Goals, intent.RequestedMarkets);
        Assert.Contains("Arsenal", intent.TeamHints);
    }

    [Fact]
    public void Parse_NoMarketOrTeamSpecified_ReturnsClarification()
    {
        var intent = QuickSlipParser.Parse("Give me 5 games");

        Assert.True(intent.IsAmbiguous);
        Assert.Contains(intent.ClarificationQuestions, q => q.Contains("Which market", StringComparison.OrdinalIgnoreCase));
    }
}
