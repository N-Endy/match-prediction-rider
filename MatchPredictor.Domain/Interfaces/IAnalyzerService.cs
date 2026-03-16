namespace MatchPredictor.Domain.Interfaces;

public interface IAnalyzerService
{
    Task ExtractDataAndSyncDatabaseAsync(int predictionDayOffset = 0, string? runReason = null);
    Task GeneratePredictionsAsync(string? targetDate = null, string? runReason = null);
    Task RunScoreUpdaterAsync(int lookbackDays = 1, string runLabel = "recent");
    Task RunDailyAnalysisAsync();
    Task CleanupOldPredictionsAndMatchDataAsync();
    Task BackfillStoredPredictionTimesAsync(int lookbackDays = 90);
}
