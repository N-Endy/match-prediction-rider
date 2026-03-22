using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure;
using MatchPredictor.Infrastructure.Services;
using MatchPredictor.Infrastructure.Utils;
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
                new System.Collections.Generic.KeyValuePair<string, string>("ScrapingValues:AiScoreWebsite", "https://m.aiscore.com/tennis")
            });
            var config = configBuilder.Build();

            var scraper = new WebScraperService(
                config,
                NullLogger<WebScraperService>.Instance,
                new AiScoreSourceHealthTracker(),
                new SofaScoreSourceHealthTracker());

            var scores = await scraper.ScrapeAiScoreMatchScoresAsync();

            _output.WriteLine($"Extracted {scores.Count} live matches from AiScore");
            
            foreach (var match in scores)
            {
                _output.WriteLine($"[{match.League}] {match.HomeTeam} {match.Score} {match.AwayTeam} (Live: {match.IsLive})");
            }

            // We can't guarantee there are live matches right now, so we just ensure it didn't throw an exception.
            Assert.NotNull(scores);
        }

        [Fact]
        public void ParseAiScoreNuxtState_SkipsOverlyLargePayload()
        {
            var scraper = CreateScraper();
            var parseMethod = typeof(WebScraperService).GetMethod(
                "ParseAiScoreNuxtState",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(parseMethod);

            var oversizedPayload = new string('a', 5 * 1024 * 1024 + 2048);
            var html = $"<script>window.__NUXT__={{state:{{'tennis/home':{{matchesData_matches:[],matchesData_teams:[],matchesData_competitions:[],pad:'{oversizedPayload}'}}}}}};</script>";

            var result = (System.Collections.Generic.List<AiScoreMatchScore>)parseMethod!.Invoke(scraper, new object[] { html })!;

            Assert.Empty(result);
        }

        [Fact]
        public void ResolveFlashScoreTennisUrl_AppendsDayOffsetQuery()
        {
            var resolveMethod = typeof(WebScraperService).GetMethod(
                "ResolveFlashScoreTennisUrl",
                BindingFlags.Static | BindingFlags.NonPublic);

            Assert.NotNull(resolveMethod);

            var url = (string)resolveMethod!.Invoke(null, new object?[] { "https://www.flashscore.mobi", -1 })!;

            Assert.Equal("https://www.flashscore.mobi/tennis?d=-1", url);
        }

        [Fact]
        public void ParseFlashScoreScoreDataHtml_ExtractsMatchesAndNormalizesSetTallies()
        {
            var scraper = CreateScraper();
            var parseMethod = typeof(WebScraperService).GetMethod(
                "ParseFlashScoreScoreDataHtml",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(parseMethod);

            var scoreDataHtml = """
                <h4>ATP Miami Standings</h4>
                <span>18:30</span>
                Novak Djokovic - Carlos Alcaraz
                <a class="fin" href="/match/1">fin</a>
                (6:4, 6:3)
                <br />
                <span class="live">20:15</span>
                Jannik Sinner - Daniil Medvedev
                <a class="live" href="/match/2">live</a>
                (6:4, 3:2)
                """;

            var result = (System.Collections.Generic.List<MatchScore>)parseMethod!.Invoke(
                scraper,
                new object[] { scoreDataHtml, DateTimeProvider.GetLocalDate(), false })!;

            Assert.Collection(
                result,
                finished =>
                {
                    Assert.Equal("ATP Miami", finished.League);
                    Assert.Equal("Novak Djokovic", finished.HomeTeam);
                    Assert.Equal("Carlos Alcaraz", finished.AwayTeam);
                    Assert.Equal("6:4, 6:3", finished.Score);
                    Assert.Equal("2:0", finished.NormalizedScoreline);
                    Assert.Equal(2, finished.HomeSetsWon);
                    Assert.Equal(0, finished.AwaySetsWon);
                    Assert.False(finished.IsLive);
                },
                live =>
                {
                    Assert.Equal("Jannik Sinner", live.HomeTeam);
                    Assert.Equal("Daniil Medvedev", live.AwayTeam);
                    Assert.Equal("6:4, 3:2", live.Score);
                    Assert.Equal("1:0", live.NormalizedScoreline);
                    Assert.Equal(1, live.HomeSetsWon);
                    Assert.Equal(0, live.AwaySetsWon);
                    Assert.True(live.IsLive);
                });
        }

        [Fact]
        public void ParseTennisScoresPageHtml_ExtractsFinishedAndLiveRowsWithFullNames()
        {
            var scraper = CreateScraper();
            var parseMethod = typeof(WebScraperService).GetMethod(
                "ParseTennisScoresPageHtml",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(parseMethod);

            var html = """
                <div id="matchList">
                  <div class="user">
                    <div class="group-title group-title-1">
                      <span class="leaRow">ATP Challenger Yokkaichi, Japan Men Singles</span>
                    </div>
                    <div class="list item_result">
                      <div class="listBox">
                        <div class="barItem"><span class="tn-txtstatus">END</span></div>
                        <span class="matchTime">11:00</span>
                      </div>
                      <div class="team">
                        <div class="elseTeamName">Ethan Quinn</div>
                        <div class="elseTeamName">Jiri Lehecka</div>
                      </div>
                      <div class="teamScore">
                        <div class="bigScore"><span class="tennis-score">0</span></div>
                        <div class="bigScore"><span class="tennis-score scoreRed">2</span></div>
                      </div>
                    </div>
                    <div class="list tn-match-live">
                      <div class="listBox">
                        <div class="barItem"><span>S2</span></div>
                        <span class="matchTime">14:30</span>
                      </div>
                      <div class="team">
                        <div class="elseTeamName">Ha Eum Lee</div>
                        <div class="elseTeamName">Ashleigh Simes</div>
                      </div>
                      <div class="teamScore">
                        <div class="bigScore"><span class="tennis-score">1</span></div>
                        <div class="bigScore"><span class="tennis-score">0</span></div>
                      </div>
                    </div>
                    <div class="list">
                      <div class="listBox">
                        <span class="matchTime">18:00 23/03</span>
                      </div>
                      <div class="team">
                        <div class="elseTeamName">Scheduled Player A</div>
                        <div class="elseTeamName">Scheduled Player B</div>
                      </div>
                      <div class="teamScore">
                        <div class="bigScore"><span class="tennis-score"></span></div>
                        <div class="bigScore"><span class="tennis-score"></span></div>
                      </div>
                    </div>
                  </div>
                </div>
                """;

            var result = (System.Collections.Generic.List<MatchScore>)parseMethod!.Invoke(
                scraper,
                new object[] { html, new DateOnly(2026, 3, 22), false })!;

            Assert.Collection(
                result,
                finished =>
                {
                    Assert.Equal("ATP Challenger Yokkaichi, Japan Men Singles", finished.League);
                    Assert.Equal("Ethan Quinn", finished.HomeTeam);
                    Assert.Equal("Jiri Lehecka", finished.AwayTeam);
                    Assert.Equal("0:2", finished.Score);
                    Assert.Equal("0:2", finished.NormalizedScoreline);
                    Assert.False(finished.IsLive);
                },
                live =>
                {
                    Assert.Equal("Ha Eum Lee", live.HomeTeam);
                    Assert.Equal("Ashleigh Simes", live.AwayTeam);
                    Assert.Equal("1:0", live.Score);
                    Assert.True(live.IsLive);
                });
        }

        private static WebScraperService CreateScraper()
        {
            var configBuilder = new ConfigurationBuilder();
            configBuilder.AddInMemoryCollection(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, string>("ScrapingValues:AiScoreWebsite", "https://m.aiscore.com/tennis"),
                new System.Collections.Generic.KeyValuePair<string, string>("ScrapingValues:SofaScoreBaseUrl", "https://www.sofascore.com"),
                new System.Collections.Generic.KeyValuePair<string, string>("ScrapingValues:ScoresWebsite", "https://www.flashscore.mobi/tennis"),
                new System.Collections.Generic.KeyValuePair<string, string>("ScrapingValues:TennisScoresWebsite", "https://tennisscores.mobi"),
                new System.Collections.Generic.KeyValuePair<string, string>("ScrapingValues:BrowserScrapingEnabled", "true")
            });

            return new WebScraperService(
                configBuilder.Build(),
                NullLogger<WebScraperService>.Instance,
                new AiScoreSourceHealthTracker(),
                new SofaScoreSourceHealthTracker());
        }

        [Fact]
        public async Task TestSofaScoreBrowserExtraction()
        {
            var scraper = CreateScraper();
            
            var fixtures = new[]
            {
                new SofaScoreFixtureRequest { HomeTeam = "Carlos Alcaraz", AwayTeam = "Novak Djokovic" },
                new SofaScoreFixtureRequest { HomeTeam = "Jannik Sinner", AwayTeam = "Daniil Medvedev" }
            };

            var scores = await scraper.ScrapeSofaScoreMatchScoresAsync(fixtures);

            _output.WriteLine($"Extracted {scores.Count} matches from SofaScore");
            foreach (var match in scores)
            {
                _output.WriteLine($"[{match.League}] {match.HomeTeam} {match.Score} {match.AwayTeam} (Live: {match.IsLive})");
            }

            Assert.NotNull(scores);
        }
    }
}
