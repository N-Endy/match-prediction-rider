using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Helpers;

public static class BetslipKinds
{
    public const int BankerSlipNumber = 0;
    public const int RolloverSlipNumber = 10;
    public const string DrawsTierLabel = "AI Draws (5)";

    public static bool IsRolloverSlip(Betslip slip) =>
        slip.SlipNumber == RolloverSlipNumber ||
        slip.TierLabel.StartsWith("Rollover", StringComparison.OrdinalIgnoreCase);

    public static bool IsBankerSlip(Betslip slip) =>
        slip.SlipNumber == BankerSlipNumber ||
        slip.TierLabel.StartsWith("Banker", StringComparison.OrdinalIgnoreCase);

    public static bool IsDrawSlip(Betslip slip) =>
        string.Equals(slip.TierLabel, DrawsTierLabel, StringComparison.Ordinal);

    public static bool IsLadderSlip(Betslip slip) =>
        !IsRolloverSlip(slip) && !IsBankerSlip(slip) && !IsDrawSlip(slip);

    public static bool MatchesSection(Betslip slip, BetslipRecordSection section) =>
        section switch
        {
            BetslipRecordSection.Rollover => IsRolloverSlip(slip),
            BetslipRecordSection.Banker => IsBankerSlip(slip),
            BetslipRecordSection.Ladder => IsLadderSlip(slip),
            BetslipRecordSection.AiDraws => IsDrawSlip(slip),
            _ => false
        };

    public static string ToSlug(BetslipRecordSection section) =>
        section switch
        {
            BetslipRecordSection.Rollover => "rollover",
            BetslipRecordSection.Banker => "banker",
            BetslipRecordSection.Ladder => "ladder",
            BetslipRecordSection.AiDraws => "draws",
            _ => "banker"
        };

    public static string ToDisplayName(BetslipRecordSection section) =>
        section switch
        {
            BetslipRecordSection.Rollover => "Rollover",
            BetslipRecordSection.Banker => "Banker",
            BetslipRecordSection.Ladder => "Ladder",
            BetslipRecordSection.AiDraws => "AI Draws",
            _ => "Banker"
        };

    public static BetslipRecordSection ParseSectionOrDefault(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "rollover" => BetslipRecordSection.Rollover,
            "ladder" => BetslipRecordSection.Ladder,
            "draws" or "aidraws" or "ai-draws" or "ai draws" => BetslipRecordSection.AiDraws,
            _ => BetslipRecordSection.Banker
        };
}
