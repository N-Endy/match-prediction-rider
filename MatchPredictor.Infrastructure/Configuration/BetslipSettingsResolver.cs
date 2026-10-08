using System.Globalization;
using MatchPredictor.Domain.Models;
using Microsoft.Extensions.Configuration;

namespace MatchPredictor.Infrastructure.Configuration;

public static class BetslipSettingsResolver
{
    public static BetslipSettings ApplyEnvironmentOverrides(BetslipSettings settings, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(configuration);

        // Banker Odds & Selection Overrides
        if (TryGetDouble(configuration, "BANKER_MIN_ODDS", "Betslips:BankerMinOdds", out var bankerMinOdds))
        {
            settings.BankerMinOdds = bankerMinOdds;
        }

        if (TryGetDouble(configuration, "BANKER_MAX_ODDS", "Betslips:BankerMaxOdds", out var bankerMaxOdds))
        {
            settings.BankerMaxOdds = bankerMaxOdds;
        }

        if (TryGetDouble(configuration, "BANKER_FALLBACK_MIN_ODDS", "Betslips:BankerFallbackMinOdds", out var bankerFbMinOdds))
        {
            settings.BankerFallbackMinOdds = bankerFbMinOdds;
        }

        if (TryGetDouble(configuration, "BANKER_FALLBACK_MAX_ODDS", "Betslips:BankerFallbackMaxOdds", out var bankerFbMaxOdds))
        {
            settings.BankerFallbackMaxOdds = bankerFbMaxOdds;
        }

        if (TryGetDouble(configuration, "BANKER_MIN_CONFIDENCE", "Betslips:BankerMinConfidence", out var bankerMinConf))
        {
            settings.BankerMinConfidence = bankerMinConf;
        }

        if (TryGetInt(configuration, "BANKER_MAX_PICKS", "Betslips:BankerMaxPicks", out var bankerMaxPicks))
        {
            settings.BankerMaxPicks = bankerMaxPicks;
        }

        if (TryGetInt(configuration, "BANKER_SHORTLIST_SIZE", "Betslips:BankerShortlistSize", out var bankerShortlistSize))
        {
            settings.BankerShortlistSize = bankerShortlistSize;
        }

        // Rollover Odds Overrides
        if (TryGetDouble(configuration, "ROLLOVER_MIN_ODDS", "Betslips:RolloverMinOdds", out var rollMinOdds))
        {
            settings.RolloverMinOdds = rollMinOdds;
        }

        if (TryGetDouble(configuration, "ROLLOVER_MAX_ODDS", "Betslips:RolloverMaxOdds", out var rollMaxOdds))
        {
            settings.RolloverMaxOdds = rollMaxOdds;
        }

        if (TryGetDouble(configuration, "ROLLOVER_FALLBACK_MIN_ODDS", "Betslips:RolloverFallbackMinOdds", out var rollFbMinOdds))
        {
            settings.RolloverFallbackMinOdds = rollFbMinOdds;
        }

        if (TryGetDouble(configuration, "ROLLOVER_FALLBACK_MAX_ODDS", "Betslips:RolloverFallbackMaxOdds", out var rollFbMaxOdds))
        {
            settings.RolloverFallbackMaxOdds = rollFbMaxOdds;
        }

        if (TryGetInt(configuration, "ROLLOVER_SHORTLIST_SIZE", "Betslips:RolloverShortlistSize", out var rollShortlistSize))
        {
            settings.RolloverShortlistSize = rollShortlistSize;
        }

        // Ladder & General Slip Overrides
        if (TryGetInt(configuration, "BETSLIP_MIN_MINUTES_BEFORE_KICKOFF", "Betslips:MinMinutesBeforeKickoff", out var minMinutes))
        {
            settings.MinMinutesBeforeKickoff = minMinutes;
        }

        if (TryGetDouble(configuration, "BETSLIP_SCREEN_MIN_SCORE", "Betslips:ScreenMinScore", out var screenMinScore))
        {
            settings.ScreenMinScore = screenMinScore;
        }

        if (TryGetDouble(configuration, "BETSLIP_LADDER_MIN_EDGE", "Betslips:LadderMinimumEdge", out var ladderMinEdge))
        {
            settings.LadderMinimumEdge = ladderMinEdge;
        }

        if (TryGetInt(configuration, "BETSLIP_MAX_SELECTIONS_PER_SLIP", "Betslips:MaxSelectionsPerSlip", out var maxSelections))
        {
            settings.MaxSelectionsPerSlip = maxSelections;
        }

        return settings;
    }

    private static bool TryGetDouble(IConfiguration config, string primaryKey, string secondaryKey, out double value)
    {
        var raw = config[primaryKey] ?? config[secondaryKey];
        if (!string.IsNullOrWhiteSpace(raw) && double.TryParse(raw, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetInt(IConfiguration config, string primaryKey, string secondaryKey, out int value)
    {
        var raw = config[primaryKey] ?? config[secondaryKey];
        if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = default;
        return false;
    }
}
