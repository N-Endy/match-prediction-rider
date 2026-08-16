namespace MatchPredictor.Domain.Models;

public class BetslipSettings
{
    public const string SectionName = "Betslips";

    public int MaxSelectionsPerSlip { get; set; } = 50;
    public int MaxSlipsPerPrediction { get; set; } = 1;
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

    /// <summary>Minimum model-vs-market edge for ladder acca legs. Banker, rollover, and value bets keep
    /// <see cref="PredictionSettings.ValueBetMinimumEdge"/> (3%).
    /// </summary>
    public double LadderMinimumEdge { get; set; } = 0.01;

    /// <summary>How many live-quoted picks are sent to the AI screener per request.</summary>
    public int ScreenBatchSize { get; set; } = 25;

    public double RolloverMinOdds { get; set; } = 1.20;
    public double RolloverMaxOdds { get; set; } = 1.50;
    public int RolloverShortlistSize { get; set; } = 15;

    /// <summary>Stake used to translate combined odds into estimated Naira payout on cards.</summary>
    public decimal ReferenceStakeNaira { get; set; } = 100m;

    public int WeekendSmallSlipCount { get; set; } = 2;
    public int WeekendMediumSlipCount { get; set; } = 2;
    public int WeekendBigSlipCount { get; set; } = 1;
    public int WeekendMegaSlipCount { get; set; } = 1;

    public double SmallMinOdds { get; set; } = 30;
    public double SmallMaxOdds { get; set; } = 100;
    public double SmallFallbackMinOdds { get; set; } = 20;
    public double SmallFallbackMaxOdds { get; set; } = 120;
    public int SmallMaxPicks { get; set; } = 18;

    public double MediumMinOdds { get; set; } = 100;
    public double MediumMaxOdds { get; set; } = 500;
    public double MediumFallbackMinOdds { get; set; } = 80;
    public double MediumFallbackMaxOdds { get; set; } = 600;
    public int MediumMaxPicks { get; set; } = 25;

    public double BigMinOdds { get; set; } = 1000;
    public double BigMaxOdds { get; set; } = 5000;
    public double BigFallbackMinOdds { get; set; } = 700;
    public double BigFallbackMaxOdds { get; set; } = 6000;
    public int BigMaxPicks { get; set; } = 35;

    public double MegaMinOdds { get; set; } = 5000;
    public double MegaMaxOdds { get; set; } = 50000;
    public double MegaFallbackMinOdds { get; set; } = 3000;
    public double MegaFallbackMaxOdds { get; set; } = 80000;
    public int MegaMaxPicks { get; set; } = 45;

    /// <summary>Weekday single ladder slip uses the Small odds band with this pick cap.</summary>
    public int DailyMaxPicks { get; set; } = 18;
}
