namespace MatchPredictor.Domain.Models;

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
    public double Similarity { get; init; }
    public string RejectionHint { get; init; } = string.Empty;
}

public sealed class ManualScoreConfirmRequest
{
    public int PredictionId { get; init; }
    public string SourceName { get; init; } = string.Empty;
    public int SourceRowId { get; init; }
}

public sealed class ManualScoreConfirmResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? ActualScore { get; init; }
    public bool IsLive { get; init; }
    public int UpdatedPredictionCount { get; init; }
    public IReadOnlyList<int> UpdatedPredictionIds { get; init; } = [];
}
