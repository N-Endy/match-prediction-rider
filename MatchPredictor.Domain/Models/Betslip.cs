namespace MatchPredictor.Domain.Models;

public class BetslipSet
{
    public int Id { get; set; }
    public DateOnly SlipLocalDate { get; set; }
    public DateTime GeneratedAtUtc { get; set; }
    /// <summary>"morning" or "midday".</summary>
    public string RunLabel { get; set; } = string.Empty;
    /// <summary>"Weekend" or "Weekday".</summary>
    public string DayKind { get; set; } = string.Empty;
    public bool IsCurrent { get; set; } = true;
    public int SlipCount { get; set; }
    public List<Betslip> Slips { get; set; } = [];
}

public class Betslip
{
    public int Id { get; set; }
    public int BetslipSetId { get; set; }
    public BetslipSet? BetslipSet { get; set; }
    public int SlipNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string TierLabel { get; set; } = string.Empty;
    public int TargetMinSelections { get; set; }
    public int TargetMaxSelections { get; set; }
    public int SelectionCount { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public string BookingUrl { get; set; } = string.Empty;
    /// <summary>Booked / Partial / Failed.</summary>
    public string BookingStatus { get; set; } = BetslipBookingStatuses.Failed;
    public string StatusMessage { get; set; } = string.Empty;
    public DateTime? EarliestKickoffUtc { get; set; }
    public double? CombinedDecimalOdds { get; set; }
    public string? AiSummary { get; set; }
    public List<BetslipSelection> Selections { get; set; } = [];
}

public class BetslipSelection
{
    public int Id { get; set; }
    public int BetslipId { get; set; }
    public Betslip? Betslip { get; set; }
    public int? PredictionId { get; set; }
    public string League { get; set; } = string.Empty;
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    /// <summary>Cart/booking market code: BTTS, Over2.5, Under2.5, StraightWin, 1X2.</summary>
    public string Market { get; set; } = string.Empty;
    public string PredictedOutcome { get; set; } = string.Empty;
    public decimal? ConfidenceScore { get; set; }
    public DateTime? MatchDateTimeUtc { get; set; }
    public double? DecimalOdds { get; set; }
    public string? AiNote { get; set; }
    public bool WasBooked { get; set; }
}

public static class BetslipBookingStatuses
{
    public const string Booked = "Booked";
    public const string Partial = "Partial";
    public const string Failed = "Failed";
}

public static class BetslipDayKinds
{
    public const string Weekend = "Weekend";
    public const string Weekday = "Weekday";
}

public static class BetslipRunLabels
{
    public const string Morning = "morning";
    public const string Midday = "midday";
}
