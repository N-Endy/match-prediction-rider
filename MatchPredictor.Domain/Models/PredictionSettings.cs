namespace MatchPredictor.Domain.Models;

public class PredictionSettings
{
    public double HomeWinStrong { get; set; } = 0.68;
    public double AwayWinStrong { get; set; } = 0.70;
    public double BttsScoreThreshold { get; set; } = 0.55;
    public double OverTwoGoalsStrongThreshold { get; set; } = 0.58;
    public double UnderTwoGoalsStrongThreshold { get; set; } = 0.58;
    public double DrawStrongThreshold { get; set; } = 0.45;
    public double MinTotalXgRequiredForWin { get; set; } = 2.0;
    public double ValueBetMinimumEdge { get; set; } = 0.03;

    /// <summary>
    /// Down-rank markets in betslip packing when recent settled ROI is below this percent
    /// (requires enough samples). Does not remove picks from public pages or the live-quoted pool.
    /// </summary>
    public double SuppressPublishBelowRoiPercent { get; set; } = -5.0;

    /// <summary>
    /// Down-rank markets in betslip packing when average closing-line value is below this percent
    /// (requires enough publish+close snapshot pairs).
    /// </summary>
    public double SuppressPublishBelowClvPercent { get; set; } = -2.0;

    /// <summary>Minimum settled bets before ROI/CLV betslip soft-suppression applies.</summary>
    public int SuppressPublishMinSettledBets { get; set; } = 20;

    /// <summary>
    /// When true and a market has a promoted ML profile, down-weight the hand-tuned ProbabilityCalculator
    /// ("Market") signal toward zero so bookmaker + statistical + ML dominate.
    /// </summary>
    public bool RetireProbabilityCalculatorWhenMlPromoted { get; set; } = true;
}
