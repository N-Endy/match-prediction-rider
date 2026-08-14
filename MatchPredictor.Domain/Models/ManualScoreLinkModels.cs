namespace MatchPredictor.Domain.Models;

public sealed record ScoreNearMissHintSet
{
    public int PredictionId { get; init; }
    public IReadOnlyList<ScoreNearMissHint> Candidates { get; init; } = [];
}

public sealed record ScoreNearMissHint
{
    public int PredictionId { get; init; }
    public string SourceName { get; init; } = string.Empty;
    public int SourceRowId { get; init; }
    public string? SourceEventId { get; init; }
    public string ScrapedHomeTeam { get; init; } = string.Empty;
    public string ScrapedAwayTeam { get; init; } = string.Empty;
    public string ScrapedLeague { get; init; } = string.Empty;
    public string Score { get; init; } = string.Empty;
    public bool IsLive { get; init; }
    public bool IsFlipped { get; init; }
    public double Similarity { get; init; }
    public string RejectionHint { get; init; } = string.Empty;
}

public sealed class ManualScoreConfirmRequest
{
    public int PredictionId { get; init; }
    public string SourceName { get; init; } = string.Empty;
    public int SourceRowId { get; init; }
}

public sealed class ManualScoreConfirmPredictionUpdate
{
    public int PredictionId { get; init; }
    public string ScoreClass { get; init; } = "mp-score-incorrect";
    public bool IsLive { get; init; }
}

public sealed class ManualScoreConfirmResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? ActualScore { get; init; }
    public bool IsLive { get; init; }
    public int UpdatedPredictionCount { get; init; }
    public IReadOnlyList<int> UpdatedPredictionIds { get; init; } = [];
    public IReadOnlyList<ManualScoreConfirmPredictionUpdate> Updates { get; init; } = [];
}
