using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IWebScraperService
{
    Task ScrapeMatchDataAsync();
    Task<List<MatchScore>> ScrapeMatchScoresAsync();
    Task<List<MatchScore>> ScrapeTennisScoresMatchScoresAsync();
    Task<List<AiScoreMatchScore>> ScrapeAiScoreMatchScoresAsync();
    Task<List<SofaScoreMatchScore>> ScrapeSofaScoreMatchScoresAsync(IEnumerable<SofaScoreFixtureRequest> fixtures);
}
