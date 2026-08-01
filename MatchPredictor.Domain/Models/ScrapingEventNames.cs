namespace MatchPredictor.Domain.Models;

/// <summary>
/// Canonical event names written to <see cref="ScrapingLog"/>. Shared between the
/// services that emit them and the health page that monitors them so the two
/// can never drift apart.
/// </summary>
public static class ScrapingEventNames
{
    public const string DataSync = "data_sync";
    public const string PredictionGeneration = "prediction_generation";
    public const string DailyAnalysis = "daily_analysis";
    public const string SourceQuality = "source_quality";
    public const string ClosingLineSnapshot = "closing_line_snapshot";
    public const string AiScoreRuntime = "source_runtime_aiscore";
    public const string SofaScoreRuntime = "source_runtime_sofascore";
    public const string ScoreUpdateRecent = "score_update_recent";
    public const string ScoreUpdateBackfill = "score_update_backfill";
    public const string BetslipGeneration = "betslip_generation";

    public static string ScoreUpdate(string? runLabel) =>
        $"score_update_{(string.IsNullOrWhiteSpace(runLabel) ? "recent" : runLabel.Trim().ToLowerInvariant())}";
}
