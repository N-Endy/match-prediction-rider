using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IWebScraperService
{
    Task ScrapeMatchDataAsync();

    /// <param name="listingDates">
    /// FlashScore listing calendar dates (WAT) to fetch via <c>?d=N</c>.
    /// When null or empty, defaults to today.
    /// </param>
    Task<List<MatchScore>> ScrapeMatchScoresAsync(IEnumerable<DateOnly>? listingDates = null);

    Task<List<AiScoreMatchScore>> ScrapeAiScoreMatchScoresAsync();
    Task<List<SofaScoreMatchScore>> ScrapeSofaScoreMatchScoresAsync(IEnumerable<SofaScoreFixtureRequest> fixtures);
}
