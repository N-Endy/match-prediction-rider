using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class BetslipSettingsResolverTests
{
    [Fact]
    public void ApplyEnvironmentOverrides_FlatRailwayEnvironmentVariables_OverridesSettings()
    {
        var settings = new BetslipSettings
        {
            BankerMinOdds = 5.0,
            BankerMaxOdds = 10.0,
            BankerFallbackMinOdds = 4.0,
            BankerFallbackMaxOdds = 12.0,
            BankerMinConfidence = 0.65,
            BankerMaxPicks = 8,
            BankerShortlistSize = 20,
            RolloverMinOdds = 1.30,
            RolloverMaxOdds = 1.50,
            RolloverFallbackMinOdds = 1.15,
            RolloverFallbackMaxOdds = 1.80,
            RolloverShortlistSize = 15,
            MinMinutesBeforeKickoff = 20,
            ScreenMinScore = 40.0,
            LadderMinimumEdge = 0.01,
            MaxSelectionsPerSlip = 50
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BANKER_MIN_ODDS"] = "2.5",
                ["BANKER_MAX_ODDS"] = "6.0",
                ["BANKER_FALLBACK_MIN_ODDS"] = "2.0",
                ["BANKER_FALLBACK_MAX_ODDS"] = "8.0",
                ["BANKER_MIN_CONFIDENCE"] = "0.60",
                ["BANKER_MAX_PICKS"] = "5",
                ["BANKER_SHORTLIST_SIZE"] = "25",
                ["ROLLOVER_MIN_ODDS"] = "1.25",
                ["ROLLOVER_MAX_ODDS"] = "1.55",
                ["ROLLOVER_FALLBACK_MIN_ODDS"] = "1.10",
                ["ROLLOVER_FALLBACK_MAX_ODDS"] = "1.75",
                ["ROLLOVER_SHORTLIST_SIZE"] = "18",
                ["BETSLIP_MIN_MINUTES_BEFORE_KICKOFF"] = "15",
                ["BETSLIP_SCREEN_MIN_SCORE"] = "35",
                ["BETSLIP_LADDER_MIN_EDGE"] = "0.02",
                ["BETSLIP_MAX_SELECTIONS_PER_SLIP"] = "30"
            })
            .Build();

        var result = BetslipSettingsResolver.ApplyEnvironmentOverrides(settings, config);

        Assert.Equal(2.5, result.BankerMinOdds);
        Assert.Equal(6.0, result.BankerMaxOdds);
        Assert.Equal(2.0, result.BankerFallbackMinOdds);
        Assert.Equal(8.0, result.BankerFallbackMaxOdds);
        Assert.Equal(0.60, result.BankerMinConfidence);
        Assert.Equal(5, result.BankerMaxPicks);
        Assert.Equal(25, result.BankerShortlistSize);

        Assert.Equal(1.25, result.RolloverMinOdds);
        Assert.Equal(1.55, result.RolloverMaxOdds);
        Assert.Equal(1.10, result.RolloverFallbackMinOdds);
        Assert.Equal(1.75, result.RolloverFallbackMaxOdds);
        Assert.Equal(18, result.RolloverShortlistSize);

        Assert.Equal(15, result.MinMinutesBeforeKickoff);
        Assert.Equal(35.0, result.ScreenMinScore);
        Assert.Equal(0.02, result.LadderMinimumEdge);
        Assert.Equal(30, result.MaxSelectionsPerSlip);
    }

    [Fact]
    public void ApplyEnvironmentOverrides_WhenNoOverrides_PreservesOriginalValues()
    {
        var settings = new BetslipSettings
        {
            BankerMinOdds = 3.5,
            BankerMaxOdds = 7.0
        };

        var config = new ConfigurationBuilder().Build();

        var result = BetslipSettingsResolver.ApplyEnvironmentOverrides(settings, config);

        Assert.Equal(3.5, result.BankerMinOdds);
        Assert.Equal(7.0, result.BankerMaxOdds);
    }

    [Fact]
    public void ApplyEnvironmentOverrides_InvalidNumericValues_IgnoredSafely()
    {
        var settings = new BetslipSettings
        {
            BankerMinOdds = 3.0
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BANKER_MIN_ODDS"] = "not-a-number"
            })
            .Build();

        var result = BetslipSettingsResolver.ApplyEnvironmentOverrides(settings, config);

        Assert.Equal(3.0, result.BankerMinOdds);
    }
}
