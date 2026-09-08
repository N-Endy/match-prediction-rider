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
        [Trait("Category", "LiveNetwork")]
        public async Task TestAiScoreHttpExtraction()
        {
            var configBuilder = new ConfigurationBuilder();
            configBuilder.AddInMemoryCollection(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, string>("ScrapingValues:AiScoreWebsite", "https://m.aiscore.com")
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
        public void ParseScoreDataHtml_ExtractsFinishedAndLiveScoresWithHyphenSeparator()
        {
            // Mirrors flashscore.mobi's current markup: scores use a hyphen ("2-1"),
            // scheduled rows use "sched", finished rows "fin", in-play rows "live".
            const string rawHtml =
                "<h4>ARGENTINA: Primera</h4>" +
                "<span>17:00</span>Kairat Almaty (Kaz) - Sutjeska (Mne) <a href=\"/match/a/\" class=\"sched\">&nbsp;-&nbsp;</a><br />" +
                "<span>20:45</span>Caicara U20 - Comercial PI U20 <a href=\"/match/b/\" class=\"fin\">0-3</a><br />" +
                "<span class=\"live\">83'</span>GV San Jose - Tomayapo <a href=\"/match/c/\" class=\"live\">2-2</a><br />";

            var result = WebScraperService.ParseScoreDataHtml(rawHtml);

            Assert.Equal(2, result.Count);

            var finished = result.Find(s => s.HomeTeam == "Caicara U20");
            Assert.NotNull(finished);
            Assert.Equal("0:3", finished!.Score);
            Assert.False(finished.IsLive);

            var live = result.Find(s => s.HomeTeam == "GV San Jose");
            Assert.NotNull(live);
            Assert.Equal("2:2", live!.Score);
            Assert.True(live.IsLive);
            Assert.True(live.BTTSLabel);
        }

        [Fact]
        public void ParseScoreDataHtml_ExtractsMissingSpaceTeamHyphenAndAetPenScores()
        {
            const string rawHtml =
                "<h4>SPAIN: LaLiga</h4>" +
                "<span>19:00</span>Getafe- Celta Vigo <a href=\"/match/a/\" class=\"fin\">1-1</a><br />" +
                "<span>20:45</span>Weston-super-Mare - AFC Totton <a href=\"/match/b/\" class=\"fin\">3-1</a><br />" +
                "<span>20:45</span>Portishead Town - Wimborne <a href=\"/match/c/\" class=\"fin\">1-3aet</a><br />" +
                "<span>20:30</span>Worthing - Norwich U21 <a href=\"/match/d/\" class=\"fin\">0-0 (Pen: 4-5)</a><br />" +
                "<span>20:45</span>Lille- Betis <a href=\"/match/e/\">2-3</a><br />";

            var result = WebScraperService.ParseScoreDataHtml(rawHtml);

            Assert.Equal(5, result.Count);

            var getafe = result.Find(s => s.HomeTeam == "Getafe");
            Assert.NotNull(getafe);
            Assert.Equal("Celta Vigo", getafe!.AwayTeam);
            Assert.Equal("1:1", getafe.Score);

            var weston = result.Find(s => s.HomeTeam == "Weston-super-Mare");
            Assert.NotNull(weston);
            Assert.Equal("AFC Totton", weston!.AwayTeam);
            Assert.Equal("3:1", weston.Score);

            var aet = result.Find(s => s.HomeTeam == "Portishead Town");
            Assert.NotNull(aet);
            Assert.Equal("1:3", aet!.Score);

            var pens = result.Find(s => s.HomeTeam == "Worthing");
            Assert.NotNull(pens);
            Assert.Equal("0:0", pens!.Score);
            Assert.False(pens.BTTSLabel);

            var lille = result.Find(s => s.HomeTeam == "Lille");
            Assert.NotNull(lille);
            Assert.Equal("Betis", lille!.AwayTeam);
            Assert.Equal("2:3", lille.Score);
        }

        [Fact]
        public void ParseScoreMatchTime_UsesListingDateForYesterdayOffset()
        {
            var today = DateOnly.FromDateTime(DateTimeProvider.GetLocalTime());
            var yesterday = today.AddDays(-1);

            var matchTimeUtc = WebScraperService.ParseScoreMatchTime("20:45", isLive: false, yesterday);
            var local = DateTimeProvider.ConvertUtcToLocal(matchTimeUtc);

            Assert.Equal(yesterday, DateOnly.FromDateTime(local));
            Assert.Equal(20, local.Hour);
            Assert.Equal(45, local.Minute);
        }

        [Fact]
        public void BuildFlashScoreListingUrl_AppendsDayOffsetQuery()
        {
            Assert.Equal(
                "https://www.flashscore.mobi",
                WebScraperService.BuildFlashScoreListingUrl("https://www.flashscore.mobi/", 0));
            Assert.Equal(
                "https://www.flashscore.mobi/?d=-1",
                WebScraperService.BuildFlashScoreListingUrl("https://www.flashscore.mobi", -1));
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
            var html = $"<script>window.__NUXT__={{state:{{'football/home':{{matchesData_matches:[],matchesData_teams:[],matchesData_competitions:[],pad:'{oversizedPayload}'}}}}}};</script>";

            var result = (System.Collections.Generic.List<AiScoreMatchScore>)parseMethod!.Invoke(scraper, new object[] { html })!;

            Assert.Empty(result);
        }

        private static WebScraperService CreateScraper()
        {
            var configBuilder = new ConfigurationBuilder();
            configBuilder.AddInMemoryCollection(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, string>("ScrapingValues:AiScoreWebsite", "https://m.aiscore.com"),
                new System.Collections.Generic.KeyValuePair<string, string>("ScrapingValues:SofaScoreWebsite", "https://www.sofascore.com"),
                new System.Collections.Generic.KeyValuePair<string, string>("ScrapingValues:ScoresWebsite", "https://www.sofascore.com"),
                new System.Collections.Generic.KeyValuePair<string, string>("ScrapingValues:BrowserScrapingEnabled", "true")
            });

            return new WebScraperService(
                configBuilder.Build(),
                NullLogger<WebScraperService>.Instance,
                new AiScoreSourceHealthTracker(),
                new SofaScoreSourceHealthTracker());
        }

        [Fact]
        [Trait("Category", "LiveNetwork")]
        public async Task TestSofaScoreBrowserExtraction()
        {
            var scraper = CreateScraper();
            
            var fixtures = new[]
            {
                new SofaScoreFixtureRequest { HomeTeam = "Lyon", AwayTeam = "Celta" },
                new SofaScoreFixtureRequest { HomeTeam = "Freiburg", AwayTeam = "Genk" }
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
