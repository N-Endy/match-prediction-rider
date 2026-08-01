namespace MatchPredictor.Domain.Models;

public class BetslipSettings
{
    public const string SectionName = "Betslips";

    public int MaxSelectionsPerSlip { get; set; } = 50;
    public int MaxSlipsPerPrediction { get; set; } = 3;
    public int MinMinutesBeforeKickoff { get; set; } = 20;
    public double OverProvisionFactor { get; set; } = 1.2;
    public double MaxSingleMarketShare { get; set; } = 0.4;
    public int BookingDelayMilliseconds { get; set; } = 750;
    public double OverlapPenalty { get; set; } = 0.08;
    public int DrawCandidatePoolSize { get; set; } = 12;
    public int DrawSlipSize { get; set; } = 5;
}
