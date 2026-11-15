namespace MatchPredictor.Domain.Models;

public class PredictionSettings
{
    public double HomeWinStrong { get; set; } = 0.68;
    public double AwayWinStrong { get; set; } = 0.70;
    public double BttsScoreThreshold { get; set; } = 0.55;
    public double OverTwoGoalsStrongThreshold { get; set; } = 0.58;
    public double UnderTwoGoalsStrongThreshold { get; set; } = 0.58;
    public double DrawStrongThreshold { get; set; } = 0.30;
    public double OverOneFiveStrongThreshold { get; set; } = 0.74;
    public double DoubleChanceStrongThreshold { get; set; } = 0.74;

    /// <summary>
    /// Minimum Dominance Ratio (P(Win) / (P(Draw) + P(Loss))) required to publish a Straight Win.
    /// Filters out draw-contaminated favorites where failure risk is high.
    /// Set to 0.0 to disable.
    /// </summary>
    public double MinDominanceRatioForWin { get; set; } = 1.25;

    /// <summary>
    /// Minimum Win-to-Draw probability ratio required to publish a Straight Win.
    /// Set to 0.0 to disable.
    /// </summary>
    public double MinWinToDrawRatio { get; set; } = 1.70;

    /// <summary>
    /// Maximum allowed Under 2.5 calibrated probability for publishing Over 2.5 Goals.
    /// Eliminates the "2-goal cluster trap" (1-1, 2-0).
    /// </summary>
    public double MaxUnder25ProbForOver25 { get; set; } = 0.45;

    /// <summary>
    /// Maximum favorite win probability allowed when publishing Under 2.5 Goals.
    /// In heavy blowouts (favorite win > 0.72), the favorite alone can produce 3+ goals.
    /// </summary>
    public double MaxFavoriteWinProbForUnder25 { get; set; } = 0.72;

    /// <summary>
    /// Maximum allowed win probability for the underdog when publishing Double Chance (1X or X2).
    /// </summary>
    public double MaxUnderdogProbabilityForDoubleChance { get; set; } = 0.22;

    /// <summary>
    /// When true, enables qualifying and publishing Over 1.5 Goals banker selections.
    /// </summary>
    public bool EnableOver15Predictions { get; set; } = true;

    /// <summary>
    /// When true, enables qualifying and publishing Double Chance (1X, X2) selections.
    /// </summary>
    public bool EnableDoubleChancePredictions { get; set; } = true;

    /// <summary>
    /// When true, also allows publishing Double Chance "12" (no draw) in attacking open fixtures.
    /// </summary>
    public bool EnableDoubleChance12 { get; set; } = false;

    /// <summary>
    /// Minimum Draw Affinity Index (P(Under 2.5) * (1 - |P_Home - P_Away|)) required to publish a Draw.
    /// Filters out volatile high-scoring or lopsided matches. Set to 0.0 to disable.
    /// </summary>
    public double MinDrawAffinity { get; set; } = 0.38;

    /// <summary>
    /// Maximum allowed Over 2.5 calibrated probability for publishing a Draw.
    /// Draws in matches with elevated goal expectancy have very low empirical hit rates (<18%).
    /// Set to 1.0 to disable.
    /// </summary>
    public double MaxOver25ProbabilityForDraw { get; set; } = 0.52;

    /// <summary>
    /// Minimum BTTS Affinity Index required to publish a BTTS Yes pick.
    /// Filters out lopsided blowouts (3-0, 4-0) where the underdog is held scoreless.
    /// Set to 0.0 to disable.
    /// </summary>
    public double MinBttsAffinity { get; set; } = 0.46;

    /// <summary>
    /// Minimum Over 1.5 calibrated probability required to publish a BTTS Yes pick.
    /// BTTS physically requires at least 2 goals. Set to 0.0 to disable.
    /// </summary>
    public double MinOver15ForBttsYes { get; set; } = 0.70;

    /// <summary>
    /// Maximum allowed favourite win-probability spread (|P_Home - P_Away|) for publishing BTTS Yes.
    /// In heavy mismatches (spread > 0.45), clean sheet risk for the favourite spikes dramatically.
    /// Set to 1.0 to disable.
    /// </summary>
    public double MaxSpreadForBttsYes { get; set; } = 0.45;

    /// <summary>
    /// When true, enables qualifying and publishing "No BTTS" predictions when clean-sheet or
    /// low-total dynamics dominate.
    /// </summary>
    public bool EnableBttsNoPredictions { get; set; } = true;

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

    /// <summary>
    /// Maximum aggregate Kelly exposure allowed across all concurrent bets in a single kickoff window.
    /// Default 0.20 (20% of bankroll).
    /// </summary>
    public double MaxConcurrentWindowExposureFraction { get; set; } = 0.20;

    /// <summary>
    /// Maximum minutes separating fixtures to be considered concurrent in the simultaneous Kelly solver.
    /// </summary>
    public double ConcurrentWindowToleranceMinutes { get; set; } = 45.0;

    /// <summary>
    /// When true, enforce single highest-edge pick per match in the simultaneous Kelly solver.
    /// </summary>
    public bool EnforceFixtureExclusivityInSimultaneousKelly { get; set; } = true;
}
