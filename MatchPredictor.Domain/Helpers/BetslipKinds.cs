using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Helpers;

public static class BetslipKinds
{
    public const int BankerSlipNumber = 0;
    public const int RolloverSlipNumber = 10;
    public const int SgmSlipNumber = 20;
    public const string DrawsTierLabel = "AI Draws (5)";

    public const int SynergyUnderFortressSlipNumber = 101;
    public const int SynergyFavoriteWinSlipNumber = 102;
    public const int SynergyFavoriteOverSlipNumber = 103;
    public const int SynergyGoalFrenzyOverSlipNumber = 104;
    public const int SynergyGoalFrenzyBttsSlipNumber = 105;
    public const int SynergyDrawBttsStrikeSlipNumber = 106;

    public const string SynergyUnderFortressTierLabel = "Synergy: Under 2.5 Fortress";
    public const string SynergyFavoriteWinTierLabel = "Synergy: Favorite Machine (Win)";
    public const string SynergyFavoriteOverTierLabel = "Synergy: Favorite Machine (Over 2.5)";
    public const string SynergyGoalFrenzyOverTierLabel = "Synergy: Goal Frenzy (Over 2.5)";
    public const string SynergyGoalFrenzyBttsTierLabel = "Synergy: Goal Frenzy (BTTS)";
    public const string SynergyDrawBttsStrikeTierLabel = "Synergy: Draw/BTTS Strike (BTTS)";

    public static bool IsSynergySlip(Betslip slip) =>
        slip.IsPrivate ||
        IsSynergySlip(slip.SlipNumber) ||
        slip.TierLabel.StartsWith("Synergy:", StringComparison.OrdinalIgnoreCase);

    public static bool IsSynergySlip(int slipNumber) =>
        slipNumber >= SynergyUnderFortressSlipNumber && slipNumber <= SynergyDrawBttsStrikeSlipNumber;

    public static bool IsRolloverSlip(Betslip slip) =>
        !slip.IsPrivate &&
        (slip.SlipNumber == RolloverSlipNumber ||
        slip.TierLabel.StartsWith("Rollover", StringComparison.OrdinalIgnoreCase));

    public static bool IsBankerSlip(Betslip slip) =>
        !slip.IsPrivate &&
        (slip.SlipNumber == BankerSlipNumber ||
        slip.TierLabel.StartsWith("Banker", StringComparison.OrdinalIgnoreCase));

    public static bool IsDrawSlip(Betslip slip) =>
        !slip.IsPrivate &&
        string.Equals(slip.TierLabel, DrawsTierLabel, StringComparison.Ordinal);

    public static bool IsBetBuilderSlip(Betslip slip) =>
        !slip.IsPrivate &&
        (slip.SlipNumber == SgmSlipNumber ||
        slip.TierLabel.Contains("Bet Builder", StringComparison.OrdinalIgnoreCase) ||
        slip.TierLabel.Contains("SGM", StringComparison.OrdinalIgnoreCase));

    public static bool IsLadderSlip(Betslip slip) =>
        !slip.IsPrivate && !IsSynergySlip(slip) && !IsRolloverSlip(slip) && !IsBankerSlip(slip) && !IsDrawSlip(slip) && !IsBetBuilderSlip(slip);

    public static bool MatchesSection(Betslip slip, BetslipRecordSection section) =>
        section switch
        {
            BetslipRecordSection.Rollover => IsRolloverSlip(slip),
            BetslipRecordSection.Banker => IsBankerSlip(slip),
            BetslipRecordSection.Ladder => IsLadderSlip(slip),
            BetslipRecordSection.BetBuilder => IsBetBuilderSlip(slip),
            BetslipRecordSection.AiDraws => IsDrawSlip(slip),
            _ => false
        };

    public static string ToSlug(BetslipRecordSection section) =>
        section switch
        {
            BetslipRecordSection.Rollover => "rollover",
            BetslipRecordSection.Banker => "banker",
            BetslipRecordSection.Ladder => "ladder",
            BetslipRecordSection.BetBuilder => "betbuilder",
            BetslipRecordSection.AiDraws => "draws",
            _ => "banker"
        };

    public static string ToDisplayName(BetslipRecordSection section) =>
        section switch
        {
            BetslipRecordSection.Rollover => "Rollover",
            BetslipRecordSection.Banker => "Banker",
            BetslipRecordSection.Ladder => "Ladder",
            BetslipRecordSection.BetBuilder => "Bet Builder",
            BetslipRecordSection.AiDraws => "AI Draws",
            _ => "Banker"
        };

    public static BetslipRecordSection ParseSectionOrDefault(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "rollover" => BetslipRecordSection.Rollover,
            "ladder" => BetslipRecordSection.Ladder,
            "betbuilder" or "bet-builder" or "sgm" or "builder" => BetslipRecordSection.BetBuilder,
            "draws" or "aidraws" or "ai-draws" or "ai draws" => BetslipRecordSection.AiDraws,
            _ => BetslipRecordSection.Banker
        };
}
