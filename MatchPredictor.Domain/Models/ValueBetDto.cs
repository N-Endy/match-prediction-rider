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
