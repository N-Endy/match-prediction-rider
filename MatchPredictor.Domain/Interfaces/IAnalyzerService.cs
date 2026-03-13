namespace MatchPredictor.Domain.Interfaces;

public interface IAnalyzerService
{
    Task ExtractDataAndSyncDatabaseAsync();
    Task GeneratePredictionsAsync();
    Task RunScoreUpdaterAsync(int lookbackDays = 1, string runLabel = "recent");
    Task RunDailyAnalysisAsync();
    Task CleanupOldPredictionsAndMatchDataAsync();
    Task BackfillStoredPredictionTimesAsync(int lookbackDays = 90);
}
