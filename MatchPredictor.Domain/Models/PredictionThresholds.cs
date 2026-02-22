// namespace MatchPredictor.Domain.Models;
//
// public class PredictionThresholds
// {
//     public const double BalancedMatchDiff = 0.30;
//
//     public const double Over2 = 0.60;
//     public const double Over3 = 0.50;
//
//     public const double DrawProb = 0.33;
//     public const double WinCap = 0.34;
//     public const double Under2 = 0.5;
//
//     public const double HomeWinStrong = 0.6;
//     public const double AwayWinStrong = 0.62;
//     
//     public const double OverGoalsForWin = 0.6;
//     public const double UnderGoalsForControl = 0.4;
//     
//     public const double BTTSScoreThreshold = 0.6;
//     
//     public const double OverGoalsForControl = 0.4;
// }


namespace MatchPredictor.Domain.Models;

public class PredictionThresholds
{
    public const double BalancedMatchDiff = 0.30;

    // Existing (keep)
    public const double Over2 = 0.60;
    public const double Over3 = 0.50;

    public const double DrawProb = 0.33;
    public const double WinCap = 0.34;
    public const double Under2 = 0.5;

    public const double HomeWinStrong = 0.6;
    public const double AwayWinStrong = 0.62;

    public const double OverGoalsForWin = 0.6;
    public const double UnderGoalsForControl = 0.4;

    public const double BTTSScoreThreshold = 0.6;

    public const double OverGoalsForControl = 0.4;

    // ✅ NEW: tuned for calibrated probabilities
    // Over 2.5 usually lives around 0.50–0.75; 0.65 is a solid "high confidence" gate.
    public const double OverTwoGoalsStrongThreshold = 0.65;

    // Draw probabilities rarely exceed ~0.40; 0.34 is a sensible "strong draw" filter.
    public const double DrawStrongThreshold = 0.4;
}