namespace MatchPredictor.Domain.Interfaces;

public interface IAnalyzerService
{
    Task RunScraperAndAnalyzerAsync();
    Task CleanupOldPredictionsAsync();
}