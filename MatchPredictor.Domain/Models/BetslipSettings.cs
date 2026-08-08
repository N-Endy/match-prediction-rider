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

    public double BankerMinOdds { get; set; } = 5.0;
    public double BankerMaxOdds { get; set; } = 10.0;
    public double BankerFallbackMinOdds { get; set; } = 4.0;
    public double BankerFallbackMaxOdds { get; set; } = 12.0;
    public double BankerMinConfidence { get; set; } = 0.65;
    public int BankerShortlistSize { get; set; } = 20;
    public int BankerMaxPicks { get; set; } = 8;
}
