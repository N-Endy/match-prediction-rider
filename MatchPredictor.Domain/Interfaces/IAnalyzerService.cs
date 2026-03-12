namespace MatchPredictor.Domain.Interfaces;

public interface IAnalyzerService
{
    Task ExtractDataAndSyncDatabaseAsync();
    Task GeneratePredictionsAsync();
    Task RunScoreUpdaterAsync();
    Task RunDailyAnalysisAsync();
    Task CleanupOldPredictionsAndMatchDataAsync();
    Task BackfillStoredPredictionTimesAsync(int lookbackDays = 90);
}
