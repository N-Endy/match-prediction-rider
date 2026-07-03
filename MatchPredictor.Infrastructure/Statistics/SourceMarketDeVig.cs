using MatchPredictor.Domain.Models;

namespace MatchPredictor.Infrastructure.Statistics;

/// <summary>
/// Converts raw bookmaker quotes (<see cref="SourceMarketFixture"/>) into de-vigged fair
/// probabilities that can serve as an honest market signal in the ensemble.
///
/// Method selection follows the standard literature: <b>Shin</b> for the 3-outcome 1X2
/// market (models favourite-longshot bias via insider-trading proportion) and <b>Power</b>
/// for 2-outcome markets (Over/Under, BTTS). When decimal odds are unavailable the source's
/// own probabilities are used, normalized proportionally, since the margin cannot be measured.
/// </summary>
public static class SourceMarketDeVig
{
    public const string ShinPowerMethod = "Shin+Power";
    public const string ProportionalFallbackMethod = "Proportional";

    /// <summary>
    /// Produces the de-vigged market signal for a fixture. Markets whose full outcome set
    /// is not quoted are left null so the blender skips them.
    /// </summary>
    public static PartialMatchProbabilities ToFairProbabilities(SourceMarketFixture fixture)
    {
        double? fairHome = null, fairDraw = null, fairAway = null;
        if (TryDeVigOneX2(fixture, out var oneX2))
        {
            fairHome = oneX2.home;
            fairDraw = oneX2.draw;
            fairAway = oneX2.away;
        }

        double? fairOver = null, fairUnder = null;
        if (TryDeVigTwoWay(fixture.Over25Odds, fixture.Under25Odds, fixture.Over25Probability, fixture.Under25Probability, out var totals))
        {
            fairOver = totals.first;
            fairUnder = totals.second;
        }

        double? fairBtts = null;
        if (TryDeVigTwoWay(fixture.BttsYesOdds, fixture.BttsNoOdds, fixture.BttsYesProbability, fixture.BttsNoProbability, out var btts))
        {
            fairBtts = btts.first;
        }

        return new PartialMatchProbabilities(
            Btts: fairBtts,
            Over25: fairOver,
            Under25: fairUnder,
            Draw: fairDraw,
            HomeWin: fairHome,
            AwayWin: fairAway);
    }

    /// <summary>
    /// Describes which de-vig path produced the signal (for snapshot provenance).
    /// </summary>
    public static string ResolveMethod(SourceMarketFixture fixture)
    {
        var hasOneX2Odds = IsValidOdds(fixture.HomeWinOdds) && IsValidOdds(fixture.DrawOdds) && IsValidOdds(fixture.AwayWinOdds);
        var hasTotalsOdds = IsValidOdds(fixture.Over25Odds) && IsValidOdds(fixture.Under25Odds);
        var hasBttsOdds = IsValidOdds(fixture.BttsYesOdds) && IsValidOdds(fixture.BttsNoOdds);
        return hasOneX2Odds || hasTotalsOdds || hasBttsOdds ? ShinPowerMethod : ProportionalFallbackMethod;
    }

    private static bool TryDeVigOneX2(SourceMarketFixture fixture, out (double home, double draw, double away) fair)
    {
        fair = default;

        if (IsValidOdds(fixture.HomeWinOdds) && IsValidOdds(fixture.DrawOdds) && IsValidOdds(fixture.AwayWinOdds))
        {
            var probabilities = OddsMath.FairProbabilitiesShin(
                [fixture.HomeWinOdds!.Value, fixture.DrawOdds!.Value, fixture.AwayWinOdds!.Value]);
            fair = (probabilities[0], probabilities[1], probabilities[2]);
            return true;
        }

        // No raw odds — fall back to the source probabilities, proportionally normalized.
        if (fixture.HomeWinProbability is > 0 && fixture.DrawProbability is > 0 && fixture.AwayWinProbability is > 0)
        {
            var total = fixture.HomeWinProbability.Value + fixture.DrawProbability.Value + fixture.AwayWinProbability.Value;
            if (total > 0)
            {
                fair = (
                    fixture.HomeWinProbability.Value / total,
                    fixture.DrawProbability.Value / total,
                    fixture.AwayWinProbability.Value / total);
                return true;
            }
        }

        return false;
    }

    private static bool TryDeVigTwoWay(
        double? firstOdds,
        double? secondOdds,
        double? firstProbability,
        double? secondProbability,
        out (double first, double second) fair)
    {
        fair = default;

        if (IsValidOdds(firstOdds) && IsValidOdds(secondOdds))
        {
            var probabilities = OddsMath.FairProbabilitiesPower([firstOdds!.Value, secondOdds!.Value]);
            fair = (probabilities[0], probabilities[1]);
            return true;
        }

        if (firstProbability is > 0 && secondProbability is > 0)
        {
            var total = firstProbability.Value + secondProbability.Value;
            if (total > 0)
            {
                fair = (firstProbability.Value / total, secondProbability.Value / total);
                return true;
            }
        }

        return false;
    }

    private static bool IsValidOdds(double? odds) => odds is > 1.0;
}
