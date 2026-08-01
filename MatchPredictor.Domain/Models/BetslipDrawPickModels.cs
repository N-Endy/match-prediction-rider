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
