namespace MatchPredictor.Domain.Models;

public sealed class BetslipDrawPickRequest
{
    public int PredictionId { get; init; }
    public string League { get; init; } = string.Empty;
    public string HomeTeam { get; init; } = string.Empty;
    public string AwayTeam { get; init; } = string.Empty;
    public decimal Confidence { get; init; }
    public DateTime? MatchDateTimeUtc { get; init; }
    public string PredictionCategory { get; init; } = "Draw";
    public double? ResearchScore { get; init; }
    public string? ScreenReason { get; init; }
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
    public string PredictionCategory { get; init; } = string.Empty;
    public double? ResearchScore { get; init; }
    public string? ScreenReason { get; init; }
}

public sealed class BankerPickResult
{
    public IReadOnlyList<BetslipDrawPickSelection> Picks { get; init; } = [];
    public string RiskNote { get; init; } = string.Empty;
}

public sealed class LadderRankRequest
{
    public int PredictionId { get; init; }
    public string League { get; init; } = string.Empty;
    public string HomeTeam { get; init; } = string.Empty;
    public string AwayTeam { get; init; } = string.Empty;
    public string Market { get; init; } = string.Empty;
    public string PredictedOutcome { get; init; } = string.Empty;
    public string PredictionCategory { get; init; } = string.Empty;
    public decimal Confidence { get; init; }
    public double DecimalOdds { get; init; }
    public DateTime? MatchDateTimeUtc { get; init; }
    public double? ResearchScore { get; init; }
    public string? ScreenReason { get; init; }
}

public sealed class LadderRankResult
{
    public IReadOnlyList<int> OrderedPredictionIds { get; init; } = [];
}

public sealed class BetslipScreenRequest
{
    public int PredictionId { get; init; }
    public string League { get; init; } = string.Empty;
    public string HomeTeam { get; init; } = string.Empty;
    public string AwayTeam { get; init; } = string.Empty;
    public string Market { get; init; } = string.Empty;
    public string PredictedOutcome { get; init; } = string.Empty;
    public string PredictionCategory { get; init; } = string.Empty;
    public decimal Confidence { get; init; }
    public double DecimalOdds { get; init; }
    public DateTime? MatchDateTimeUtc { get; init; }
}

public sealed class BetslipScreenPick
{
    public int PredictionId { get; init; }
    public double Score { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed class BetslipScreenResult
{
    public IReadOnlyList<BetslipScreenPick> Scores { get; init; } = [];
}

public sealed class LadderComposeBandRequest
{
    public int SlipNumber { get; init; }
    public string Title { get; init; } = string.Empty;
    public string BandKey { get; init; } = string.Empty;
    public double MinOdds { get; init; }
    public double MaxOdds { get; init; }
    public double FallbackMinOdds { get; init; }
    public double FallbackMaxOdds { get; init; }
    public int MaxPicks { get; init; }
}

public sealed class LadderComposeSlip
{
    public int SlipNumber { get; init; }
    public IReadOnlyList<int> PredictionIds { get; init; } = [];
}

public sealed class LadderComposeResult
{
    public IReadOnlyList<LadderComposeSlip> Slips { get; init; } = [];
}
