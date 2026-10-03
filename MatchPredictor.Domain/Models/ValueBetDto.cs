namespace MatchPredictor.Domain.Models;

public class ValueBetDto
{
    public int? PredictionId { get; set; }
    public DateTime? MatchDateTimeUtc { get; set; }
    public string League { get; set; } = null!;
    public string HomeTeam { get; set; } = null!;
    public string AwayTeam { get; set; } = null!;
    public string KickoffTime { get; set; } = null!;
    public string PredictionCategory { get; set; } = null!;
    public string PredictedOutcome { get; set; } = null!;
    public double MathematicalProbability { get; set; }
    public double MarketProbability { get; set; }
    public double DecimalOdds { get; set; }
    public double ImpliedProbability { get; set; }
    public double ExpectedValuePercent { get; set; }
    public double Edge { get; set; }
    /// <summary>Fractional Kelly multiplier (Analytics uses 0.25 = quarter-Kelly).</summary>
    public double KellyFraction { get; set; } = BetPricingMath.DefaultKellyFraction;
    /// <summary>Suggested stake as a fraction of bankroll (e.g. 0.05 = 5%). Portfolio-optimized when multiple concurrent bets exist.</summary>
    public double KellyStakeFraction { get; set; }
    /// <summary>Unconstrained standalone fractional Kelly stake before window portfolio capping.</summary>
    public double StandaloneKellyStakeFraction { get; set; }
    /// <summary>Portfolio-adjusted simultaneous Kelly stake fraction, constrained by concurrent kickoff window risk.</summary>
    public double PortfolioKellyStakeFraction { get; set; }
    /// <summary>Number of concurrent value bets sharing this match's kickoff window.</summary>
    public int ConcurrentWindowBetCount { get; set; } = 1;
    /// <summary>Total aggregate bankroll exposure across all bets in this match's kickoff window.</summary>
    public double WindowTotalExposureFraction { get; set; }
    /// <summary>Whether this bet's stake was scaled down due to concurrent window exposure cap.</summary>
    public bool IsPortfolioCapped { get; set; }
    /// <summary>Whether this bet was excluded from simultaneous Kelly due to another pick on the same fixture having higher EV.</summary>
    public bool ExcludedDueToFixtureExclusivity { get; set; }
    public double ThresholdUsed { get; set; }
    public string ThresholdSource { get; set; } = "Configured";
    public string CalibratorUsed { get; set; } = "Bucket";
    public string PricingSource { get; set; } = "Stored sync snapshot";
    public string OddsFreshness { get; set; } = "From the latest synced pricing snapshot.";
    public string OddsDerivationSource { get; set; } = "Derived from stored sync probability";
    public string EdgeSource { get; set; } = string.Empty;
    public string AiJustification { get; set; } = null!;
    public bool? IsSettledWin { get; set; }
    public double? RealizedReturnPercent { get; set; }
    public double? ClosingLineValuePercent { get; set; }
}
