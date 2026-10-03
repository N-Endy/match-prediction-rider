namespace MatchPredictor.Domain.Models;

public enum SameGameMultiMarket
{
    HomeWin = 1,
    Draw = 2,
    AwayWin = 3,
    DoubleChance1X = 4,
    DoubleChanceX2 = 5,
    DoubleChance12 = 6,
    Over05Goals = 7,
    Over15Goals = 8,
    Over25Goals = 9,
    Over35Goals = 10,
    Under15Goals = 11,
    Under25Goals = 12,
    Under35Goals = 13,
    BothTeamsScoreYes = 14,
    BothTeamsScoreNo = 15,
    HomeCleanSheet = 16,
    AwayCleanSheet = 17,
    HomeOver15Goals = 18,
    AwayOver15Goals = 19
}

public sealed record SameGameMultiLeg(
    SameGameMultiMarket Market,
    string DisplayName,
    double? SingleOdds = null);

public sealed record SameGameMultiResult(
    bool IsValid,
    double ExactJointProbability,
    double FairDecimalOdds,
    double IndependentProbability,
    double CorrelationFactor,
    IReadOnlyList<SameGameMultiLeg> Legs,
    string? RejectionReason = null);

public static class SameGameMultiPredicates
{
    public static Func<int, int, bool> GetPredicate(SameGameMultiMarket market) => market switch
    {
        SameGameMultiMarket.HomeWin => (h, a) => h > a,
        SameGameMultiMarket.Draw => (h, a) => h == a,
        SameGameMultiMarket.AwayWin => (h, a) => h < a,
        SameGameMultiMarket.DoubleChance1X => (h, a) => h >= a,
        SameGameMultiMarket.DoubleChanceX2 => (h, a) => a >= h,
        SameGameMultiMarket.DoubleChance12 => (h, a) => h != a,
        SameGameMultiMarket.Over05Goals => (h, a) => h + a >= 1,
        SameGameMultiMarket.Over15Goals => (h, a) => h + a >= 2,
        SameGameMultiMarket.Over25Goals => (h, a) => h + a >= 3,
        SameGameMultiMarket.Over35Goals => (h, a) => h + a >= 4,
        SameGameMultiMarket.Under15Goals => (h, a) => h + a <= 1,
        SameGameMultiMarket.Under25Goals => (h, a) => h + a <= 2,
        SameGameMultiMarket.Under35Goals => (h, a) => h + a <= 3,
        SameGameMultiMarket.BothTeamsScoreYes => (h, a) => h >= 1 && a >= 1,
        SameGameMultiMarket.BothTeamsScoreNo => (h, a) => h == 0 || a == 0,
        SameGameMultiMarket.HomeCleanSheet => (h, a) => a == 0,
        SameGameMultiMarket.AwayCleanSheet => (h, a) => h == 0,
        SameGameMultiMarket.HomeOver15Goals => (h, a) => h >= 2,
        SameGameMultiMarket.AwayOver15Goals => (h, a) => a >= 2,
        _ => throw new ArgumentOutOfRangeException(nameof(market), market, "Unsupported SGM market.")
    };
}
