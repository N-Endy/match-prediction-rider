namespace MatchPredictor.Domain.Models;

public sealed class QuickSlipParseRequest
{
    public string Input { get; set; } = string.Empty;
    public DateTime? ReferenceDate { get; set; }
    public List<int>? CurrentSlipPredictionIds { get; set; }
    public int? MaxLegs { get; set; }
}

public sealed class StagedSlipLegDto
{
    public int PredictionId { get; set; }
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public string League { get; set; } = string.Empty;
    public string Market { get; set; } = string.Empty;
    public string Prediction { get; set; } = string.Empty;
    public decimal? Odds { get; set; }
    public decimal? ModelProbability { get; set; }
    public decimal? ExpectedValue { get; set; }
    public DateTime? MatchDateTimeUtc { get; set; }
    public string FormattedTime { get; set; } = string.Empty;
}

public sealed class QuickSlipParseResultDto
{
    public bool Success { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<StagedSlipLegDto> StagedPicks { get; set; } = [];
    public decimal TotalCombinedOdds { get; set; }
    public decimal AverageModelProbability { get; set; }
    public decimal? OverallExpectedEdge { get; set; }
    public List<string> ClarificationQuestions { get; set; } = [];
    public bool UsedFallback { get; set; }
    public string ActionDirective { get; set; } = "Stage"; // "Stage", "Replace", "Clarify"
}

public sealed record QuickSlipParsedIntent(
    IReadOnlyList<PredictionMarket> RequestedMarkets,
    int? RequestedCount,
    decimal? MinOdds,
    decimal? MaxOdds,
    decimal? TargetCombinedOdds,
    string? Bias, // "Safe", "Value", "Balanced"
    string? LeagueHint,
    IReadOnlyList<string> TeamHints,
    DateOnly? TargetDate,
    bool IsAmbiguous,
    IReadOnlyList<string> ClarificationQuestions);
