namespace MatchPredictor.Infrastructure.Statistics.Backtesting;

/// <summary>
/// A single out-of-sample prediction paired with its realized outcome and, optionally,
/// the price it could have been bet at (and the closing price, for CLV).
/// </summary>
public sealed record BacktestSample(
    double Probability,
    bool Outcome,
    double? DecimalOdds = null,
    double? CloseDecimalOdds = null,
    DateTime? DateUtc = null,
    double? FairMarketProbability = null);

/// <summary>
/// The full set of quality and profitability metrics produced by the backtest harness.
/// </summary>
public sealed record BacktestMetrics
{
    public int SampleCount { get; init; }

    /// <summary>Number of samples that crossed the staking threshold (i.e. actual bets).</summary>
    public int BetCount { get; init; }

    // Probabilistic quality
    public double Accuracy { get; init; }
    public double Brier { get; init; }
    public double LogLoss { get; init; }

    /// <summary>Expected Calibration Error (lower is better; 0 == perfectly calibrated).</summary>
    public double Ece { get; init; }

    // Betting performance (flat 1-unit stakes on bets above threshold)
    public double HitRate { get; init; }
    public double Roi { get; init; }
    public double Yield { get; init; }

    /// <summary>Closing-line value: average (taken odds / closing odds - 1) over bets.</summary>
    public double Clv { get; init; }

    /// <summary>Worst peak-to-trough drawdown of cumulative flat-stake P&amp;L (in units).</summary>
    public double MaxDrawdown { get; init; }

    /// <summary>Final bankroll return using fractional-Kelly staking (0 == flat / no edge).</summary>
    public double KellyRoi { get; init; }

    public static BacktestMetrics Empty { get; } = new();
}
