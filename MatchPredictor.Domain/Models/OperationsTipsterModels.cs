namespace MatchPredictor.Domain.Models;

public enum OperationsPillarKind
{
    Compounder,
    ValueEdge,
    MultiplierSniper
}

public sealed class OperationsLegDto
{
    public int PredictionId { get; set; }
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public string League { get; set; } = string.Empty;
    public DateOnly MatchLocalDate { get; set; }
    public string KickoffLocalTime { get; set; } = string.Empty;
    public DateTime? MatchDateTimeUtc { get; set; }
    public string Market { get; set; } = string.Empty;
    public string Prediction { get; set; } = string.Empty;
    public decimal Odds { get; set; }
    public decimal ModelProbability { get; set; }
    public decimal EdgePercent { get; set; }
    public string Conviction { get; set; } = "SOLID"; // BANKER, VALUE, SOLID
    public string TacticalRationale { get; set; } = string.Empty;
}

public sealed class OperationsPillarTicketDto
{
    public OperationsPillarKind PillarKind { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string TargetOddsRange { get; set; } = string.Empty;
    public decimal CombinedOdds { get; set; }
    public decimal ImpliedProbability { get; set; }
    public decimal ModelProbability { get; set; }
    public decimal EdgePercent { get; set; }
    public decimal AllocationPercent { get; set; } // e.g. 60%, 30%, 10%
    public decimal RecommendedUnits { get; set; }  // e.g. 3.0u, 1.5u, 0.5u
    public decimal KellyFraction { get; set; }     // e.g. 0.25
    public string BookingCode { get; set; } = string.Empty;
    public string BookingUrl { get; set; } = string.Empty;
    public bool IsPreserved { get; set; }
    public string PreservationReason { get; set; } = string.Empty;
    public string TacticalAnalysis { get; set; } = string.Empty;
    public List<OperationsLegDto> Legs { get; set; } = [];
}

public sealed class OperationsDossierDto
{
    public DateOnly SlateDate { get; set; }
    public DateTime GeneratedAtUtc { get; set; }
    public string GeneratedLocalLabel { get; set; } = string.Empty;
    public int TotalFixturesAnalyzed { get; set; }
    public int PublishedPicksCount { get; set; }
    public string SlateGrade { get; set; } = "GRADE B - SOLID SLATE";
    public string SlateBadgeColor { get; set; } = "#10b981";
    public string ExecutiveSummary { get; set; } = string.Empty;
    public string TacticalWatchouts { get; set; } = string.Empty;
    public decimal TotalDailyRiskBudgetPercent { get; set; } = 5.0m;
    public List<OperationsPillarTicketDto> Tickets { get; set; } = [];
}
