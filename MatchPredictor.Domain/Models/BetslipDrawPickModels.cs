namespace MatchPredictor.Domain.Models;

public sealed class BetslipDrawPickRequest
{
    public int PredictionId { get; init; }
    public string League { get; init; } = string.Empty;
    public string HomeTeam { get; init; } = string.Empty;
    public string AwayTeam { get; init; } = string.Empty;
    public decimal Confidence { get; init; }
    public DateTime? MatchDateTimeUtc { get; init; }
}

public sealed class BetslipDrawPickSelection
{
    public int PredictionId { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed class BankerPickRequest
{
    public int PredictionId { get; init; }
    public string League { get; init; } = string.Empty;
    public string HomeTeam { get; init; } = string.Empty;
    public string AwayTeam { get; init; } = string.Empty;
    public string Market { get; init; } = string.Empty;
    public string PredictedOutcome { get; init; } = string.Empty;
    public decimal Confidence { get; init; }
    public double DecimalOdds { get; init; }
    public DateTime? MatchDateTimeUtc { get; init; }
    public string? SignalSummary { get; init; }
    public bool? AllSignalsAlign { get; init; }
    public bool? ModelDivergesFromBookmaker { get; init; }
}

public sealed class BankerPickResult
{
    public IReadOnlyList<BetslipDrawPickSelection> Picks { get; init; } = [];
    public string RiskNote { get; init; } = string.Empty;
}
