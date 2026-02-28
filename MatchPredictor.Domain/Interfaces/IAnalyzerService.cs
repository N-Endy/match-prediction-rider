namespace MatchPredictor.Domain.Interfaces;

public interface IAnalyzerService
{
    Task RunPredictionGenerationAsync();
    Task RunScoreUpdaterAsync();
    Task RunDailyAnalysisAsync();
    Task CleanupOldPredictionsAndMatchDataAsync();
}