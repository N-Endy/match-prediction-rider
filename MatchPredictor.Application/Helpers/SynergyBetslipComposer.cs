using MatchPredictor.Domain.Helpers;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Application.Helpers;

public static class SynergyBetslipComposer
{
    public sealed class FixtureCandidateGroup
    {
        public string FixtureKey { get; init; } = string.Empty;
        public BetslipComposerCandidate? Draw { get; set; }
        public BetslipComposerCandidate? Under25 { get; set; }
        public BetslipComposerCandidate? Over25 { get; set; }
        public BetslipComposerCandidate? StraightWin { get; set; }
        public BetslipComposerCandidate? Btts { get; set; }
    }

    public static IReadOnlyList<ComposedBetslip> ComposeSynergySlips(
        IReadOnlyList<BetslipComposerCandidate> candidates,
        SynergySlipSettings? settings = null)
    {
        settings ??= new SynergySlipSettings();

        var fixtureGroups = new Dictionary<string, FixtureCandidateGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var key = !string.IsNullOrWhiteSpace(candidate.FixtureKey)
                ? candidate.FixtureKey
                : $"{candidate.League}|{candidate.HomeTeam}|{candidate.AwayTeam}";

            if (!fixtureGroups.TryGetValue(key, out var group))
            {
                group = new FixtureCandidateGroup { FixtureKey = key };
                fixtureGroups[key] = group;
            }

            switch (candidate.PredictionCategory)
            {
                case "Draw":
                    group.Draw = candidate;
                    break;
                case "Under2.5Goals":
                    group.Under25 = candidate;
                    break;
                case "Over2.5Goals":
                    group.Over25 = candidate;
                    break;
                case "StraightWin":
                    group.StraightWin = candidate;
                    break;
                case "BothTeamsScore":
                    group.Btts = candidate;
                    break;
            }
        }

        var underFortressPicks = new List<BetslipComposerCandidate>();
        var favoriteWinPicks = new List<BetslipComposerCandidate>();
        var favoriteOverPicks = new List<BetslipComposerCandidate>();
        var goalFrenzyOverPicks = new List<BetslipComposerCandidate>();
        var goalFrenzyBttsPicks = new List<BetslipComposerCandidate>();
        var drawBttsStrikePicks = new List<BetslipComposerCandidate>();

        foreach (var group in fixtureGroups.Values)
        {
            var drawConf = (double)(group.Draw?.Confidence ?? 0m);
            var underConf = (double)(group.Under25?.Confidence ?? 0m);
            var overConf = (double)(group.Over25?.Confidence ?? 0m);
            var winConf = (double)(group.StraightWin?.Confidence ?? 0m);
            var bttsConf = (double)(group.Btts?.Confidence ?? 0m);

            // 1. "Under 2.5 + Draw" High Confidence (Draw >= 0.35, Under >= 0.65)
            if (group.Under25 is not null &&
                drawConf >= settings.UnderFortressDrawMin &&
                underConf >= settings.UnderFortressUnderMin)
            {
                underFortressPicks.Add(group.Under25);
            }

            // 2. "Over 2.5 + Straight Win" High Confidence (SW >= 0.75, Over >= 0.65)
            if (winConf >= settings.FavoriteMachineWinMin && overConf >= settings.FavoriteMachineOverMin)
            {
                if (group.StraightWin is not null)
                {
                    favoriteWinPicks.Add(group.StraightWin);
                }
                if (group.Over25 is not null)
                {
                    favoriteOverPicks.Add(group.Over25);
                }
            }

            // 3. "BTTS + Over 2.5" High Confidence (BTTS >= 0.60, Over >= 0.65)
            if (bttsConf >= settings.GoalFrenzyBttsMin && overConf >= settings.GoalFrenzyOverMin)
            {
                if (group.Over25 is not null)
                {
                    goalFrenzyOverPicks.Add(group.Over25);
                }
                if (group.Btts is not null)
                {
                    goalFrenzyBttsPicks.Add(group.Btts);
                }
            }

            // 4. "Draw + BTTS" Strike (Draw qualified >= 0.30, BTTS >= 0.57)
            if (group.Btts is not null &&
                drawConf >= settings.DrawBttsStrikeDrawMin &&
                bttsConf >= settings.DrawBttsStrikeBttsMin)
            {
                drawBttsStrikePicks.Add(group.Btts);
            }
        }

        return
        [
            BuildSlip(
                BetslipKinds.SynergyUnderFortressSlipNumber,
                "Under 2.5 Fortress",
                BetslipKinds.SynergyUnderFortressTierLabel,
                "Exploits the statistical synergy of high-probability draws (Draw ≥ 0.35) and low total goals (Under ≥ 0.65). Empirical hit rate: 69.52%.",
                underFortressPicks,
                settings.MaxPicksPerSlip),

            BuildSlip(
                BetslipKinds.SynergyFavoriteWinSlipNumber,
                "Favorite Machine (Win)",
                BetslipKinds.SynergyFavoriteWinTierLabel,
                "Backs dominant straight wins that simultaneously project high total goals (Win ≥ 0.75, Over ≥ 0.65). Empirical hit rate: 82.49%.",
                favoriteWinPicks,
                settings.MaxPicksPerSlip),

            BuildSlip(
                BetslipKinds.SynergyFavoriteOverSlipNumber,
                "Favorite Machine (Over 2.5)",
                BetslipKinds.SynergyFavoriteOverTierLabel,
                "Takes the Over 2.5 market on heavy favorites with high goal volume (Win ≥ 0.75, Over ≥ 0.65). Empirical hit rate: 71.96%.",
                favoriteOverPicks,
                settings.MaxPicksPerSlip),

            BuildSlip(
                BetslipKinds.SynergyGoalFrenzyOverSlipNumber,
                "Goal Frenzy (Over 2.5)",
                BetslipKinds.SynergyGoalFrenzyOverTierLabel,
                "Takes the Over 2.5 market on explosive matches with dual-threat scoring (BTTS ≥ 0.60, Over ≥ 0.65). Empirical hit rate: 70.49%.",
                goalFrenzyOverPicks,
                settings.MaxPicksPerSlip),

            BuildSlip(
                BetslipKinds.SynergyGoalFrenzyBttsSlipNumber,
                "Goal Frenzy (BTTS)",
                BetslipKinds.SynergyGoalFrenzyBttsTierLabel,
                "Backs both teams to score in high-paced, high-confidence games (BTTS ≥ 0.60, Over ≥ 0.65). Empirical hit rate: 64.10%.",
                goalFrenzyBttsPicks,
                settings.MaxPicksPerSlip),

            BuildSlip(
                BetslipKinds.SynergyDrawBttsStrikeSlipNumber,
                "Draw/BTTS Strike (BTTS)",
                BetslipKinds.SynergyDrawBttsStrikeTierLabel,
                "Backs BTTS in competitive draw candidates where clean sheets are statistically rare (Draw qualified, BTTS ≥ 0.57). Empirical hit rate: 62.20%.",
                drawBttsStrikePicks,
                settings.MaxPicksPerSlip)
        ];
    }

    private static ComposedBetslip BuildSlip(
        int slipNumber,
        string title,
        string tierLabel,
        string summary,
        List<BetslipComposerCandidate> rawPicks,
        int maxPicks)
    {
        var distinctPicks = rawPicks
            .DistinctBy(p => p.PredictionId)
            .OrderByDescending(p => p.Confidence)
            .ThenBy(p => p.MatchDateTimeUtc)
            .Take(maxPicks)
            .ToList();

        var combinedOdds = distinctPicks.Count > 0 && distinctPicks.All(p => p.DecimalOdds.HasValue && p.DecimalOdds.Value > 1)
            ? distinctPicks.Aggregate(1d, (acc, p) => acc * p.DecimalOdds!.Value)
            : (double?)null;

        return new ComposedBetslip
        {
            SlipNumber = slipNumber,
            Title = title,
            TierLabel = tierLabel,
            TargetMinSelections = 1,
            TargetMaxSelections = maxPicks,
            Selections = distinctPicks,
            ShortfallNote = distinctPicks.Count == 0 ? "No qualifying fixtures today" : null,
            AiSummary = summary,
            TargetCombinedOdds = combinedOdds,
            IsPrivate = true,
            IsSynergy = true
        };
    }
}
