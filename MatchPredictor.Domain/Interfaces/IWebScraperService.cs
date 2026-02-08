using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IWebScraperService
{
    Task ScrapeMatchDataAsync();
    Task<List<MatchScore>> ScrapeMatchScoresAsync();
}