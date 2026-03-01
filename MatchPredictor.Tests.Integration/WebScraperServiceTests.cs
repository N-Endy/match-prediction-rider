using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MatchPredictor.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace MatchPredictor.Tests.Integration
{
    public class WebScraperServiceTests
    {
        private readonly ITestOutputHelper _output;

        public WebScraperServiceTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task TestAiScoreHttpExtraction()
        {
            var configBuilder = new ConfigurationBuilder();
            configBuilder.AddInMemoryCollection(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, string>("ScrapingValues:AiScoreWebsite", "https://m.aiscore.com")
            });
            var config = configBuilder.Build();

            var scraper = new WebScraperService(config, NullLogger<WebScraperService>.Instance);

            var scores = await scraper.ScrapeAiScoreMatchScoresAsync();

            _output.WriteLine($"Extracted {scores.Count} live matches from AiScore");
            
            foreach (var match in scores)
            {
                _output.WriteLine($"[{match.League}] {match.HomeTeam} {match.Score} {match.AwayTeam} (Live: {match.IsLive})");
            }

            // We can't guarantee there are live matches right now, so we just ensure it didn't throw an exception.
            Assert.NotNull(scores);
        }
    }
}
