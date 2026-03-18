using MatchPredictor.Infrastructure.Services;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class SofaScoreParsingTests
{
    [Fact]
    public void ParseEventSummaries_ReturnsRelevantLiveEvents()
    {
        const string json = """
            {
              "events": [
                {
                  "id": 101,
                  "startTimestamp": 1773763200,
                  "status": { "type": "inprogress", "description": "67'" },
                  "homeTeam": { "name": "Arsenal" },
                  "awayTeam": { "name": "Chelsea" },
                  "tournament": {
                    "name": "Premier League",
                    "category": { "name": "England" }
                  },
                  "homeScore": { "current": 2, "display": 2, "period1": 1 },
                  "awayScore": { "current": 1, "display": 1, "period1": 0 }
                },
                {
                  "id": 102,
                  "startTimestamp": 1773766800,
                  "status": { "type": "notstarted", "description": "19:00" },
                  "homeTeam": { "name": "Unused" },
                  "awayTeam": { "name": "Fixture" },
                  "tournament": {
                    "name": "Premier League",
                    "category": { "name": "England" }
                  }
                }
              ]
            }
            """;

        var events = SofaScoreApiParser.ParseEventSummaries(json, "https://www.sofascore.com");

        var summary = Assert.Single(events);
        Assert.Equal(101, summary.EventId);
        Assert.Equal("England - Premier League", summary.League);
        Assert.Equal("Arsenal", summary.HomeTeam);
        Assert.Equal("Chelsea", summary.AwayTeam);
        Assert.Equal("2:1", summary.Score);
        Assert.Equal("1:0", summary.HalfTimeScore);
        Assert.True(summary.IsLive);
    }

    [Fact]
    public void ParseResponses_ReturnsBrowserFetchResponses()
    {
        const string json = """
            [
              {
                "relativePath": "/api/v1/sport/football/events/live",
                "ok": true,
                "status": 200,
                "body": "{\"events\":[]}",
                "error": null
              }
            ]
            """;

        var responses = SofaScoreBrowserFetchParser.ParseResponses(json);

        var response = Assert.Single(responses);
        Assert.Equal("/api/v1/sport/football/events/live", response.RelativePath);
        Assert.True(response.Ok);
        Assert.Equal(200, response.Status);
    }

    [Fact]
    public void ParseEventSummaries_SplitsBrowserLiveAndScheduledPayloads()
    {
        const string liveJson = """
            {
              "events": [
                {
                  "id": 201,
                  "startTimestamp": 1773763200,
                  "status": { "type": "inprogress", "description": "52'" },
                  "homeTeam": { "name": "Arsenal" },
                  "awayTeam": { "name": "Chelsea" },
                  "tournament": {
                    "name": "Premier League",
                    "category": { "name": "England" }
                  },
                  "homeScore": { "current": 1, "display": 1, "period1": 1 },
                  "awayScore": { "current": 0, "display": 0, "period1": 0 }
                }
              ]
            }
            """;

        const string scheduledJson = """
            {
              "events": [
                {
                  "id": 202,
                  "startTimestamp": 1773766800,
                  "status": { "type": "finished", "description": "FT" },
                  "homeTeam": { "name": "Real Madrid" },
                  "awayTeam": { "name": "Barcelona" },
                  "tournament": {
                    "name": "LaLiga",
                    "category": { "name": "Spain" }
                  },
                  "homeScore": { "current": 2, "display": 2, "period1": 1 },
                  "awayScore": { "current": 1, "display": 1, "period1": 1 }
                }
              ]
            }
            """;

        var responses = new[]
        {
            new SofaScoreBrowserFetchResponse
            {
                RelativePath = "/api/v1/sport/football/events/live",
                Ok = true,
                Status = 200,
                Body = liveJson
            },
            new SofaScoreBrowserFetchResponse
            {
                RelativePath = "/api/v1/sport/football/scheduled-events/2026-03-18",
                Ok = true,
                Status = 200,
                Body = scheduledJson
            }
        };

        SofaScoreBrowserFetchParser.ParseEventSummaries(
            responses,
            "https://www.sofascore.com",
            out var liveEvents,
            out var scheduledEvents);

        Assert.Single(liveEvents);
        Assert.Single(scheduledEvents);
        Assert.Equal(201, liveEvents[0].EventId);
        Assert.Equal(202, scheduledEvents[0].EventId);
    }

    [Fact]
    public void ParseEventDetails_ParsesBrowserDetailPayloadsByEventId()
    {
        const string detailJson = """
            {
              "event": {
                "id": 501,
                "startTimestamp": 1773853200,
                "status": { "type": "finished", "description": "After extra time" },
                "homeTeam": { "name": "Madagascar" },
                "awayTeam": { "name": "Sudan" },
                "tournament": {
                  "name": "Africa Cup of Nations Qualification",
                  "category": { "name": "Africa" }
                },
                "homeScore": { "current": 1, "display": 1, "normaltime": 0, "period1": 0, "overtime": 1 },
                "awayScore": { "current": 0, "display": 0, "normaltime": 0, "period1": 0, "overtime": 0 }
              }
            }
            """;

        var details = SofaScoreBrowserFetchParser.ParseEventDetails(
            new[]
            {
                new SofaScoreBrowserFetchResponse
                {
                    RelativePath = "/api/v1/event/501",
                    Ok = true,
                    Status = 200,
                    Body = detailJson
                }
            },
            "https://www.sofascore.com");

        var parsed = Assert.Single(details);
        Assert.Equal(501, parsed.Key);
        Assert.Equal("0:0", parsed.Value.Score);
        Assert.Equal("1:0", parsed.Value.ExtraTimeScore);
    }

    [Fact]
    public void TryParseEventDetail_AfterPenalties_UsesNormalTimeScoreForSettlement()
    {
        const string json = """
            {
              "event": {
                "id": 501,
                "startTimestamp": 1773853200,
                "status": { "type": "finished", "description": "After penalties" },
                "homeTeam": { "name": "Madagascar" },
                "awayTeam": { "name": "Sudan" },
                "tournament": {
                  "name": "Africa Cup of Nations Qualification",
                  "category": { "name": "Africa" }
                },
                "homeScore": { "current": 1, "display": 1, "normaltime": 0, "period1": 0, "penalties": 1 },
                "awayScore": { "current": 0, "display": 0, "normaltime": 0, "period1": 0, "penalties": 0 }
              }
            }
            """;

        var parsed = SofaScoreApiParser.TryParseEventDetail(json, "https://www.sofascore.com", out var score);

        Assert.True(parsed);
        Assert.Equal("Africa Cup of Nations Qualification", score.League);
        Assert.Equal("0:0", score.Score);
        Assert.Equal("0:0", score.RegularTimeScore);
        Assert.Equal("1:0", score.ExtraTimeScore);
        Assert.False(score.IsLive);
    }

    [Fact]
    public void ParseRobotSitemapUrls_ReturnsSitemapLines()
    {
        const string robots = """
            User-agent: *
            Allow: /
            Sitemap: https://www.sofascore.com/sitemaps/events.xml.gz
            Sitemap: https://www.sofascore.com/sitemaps/teams.xml.gz
            """;

        var urls = SofaScoreDiscoveryHelper.ParseRobotSitemapUrls(robots);

        Assert.Equal(2, urls.Count);
        Assert.Contains("https://www.sofascore.com/sitemaps/events.xml.gz", urls);
        Assert.Contains("https://www.sofascore.com/sitemaps/teams.xml.gz", urls);
    }

    [Fact]
    public void ParseSitemapEntries_ParsesUrlSetEntries()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url>
                <loc>https://www.sofascore.com/football/match/real-madrid-manchester-city/rsEgb</loc>
                <lastmod>2026-03-17T14:00:00Z</lastmod>
              </url>
            </urlset>
            """;

        var entries = SofaScoreDiscoveryHelper.ParseSitemapEntries(System.Text.Encoding.UTF8.GetBytes(xml), "https://www.sofascore.com/sitemaps/events.xml");

        var entry = Assert.Single(entries);
        Assert.Equal("https://www.sofascore.com/football/match/real-madrid-manchester-city/rsEgb", entry.Location);
        Assert.Equal(new DateTime(2026, 3, 17, 14, 0, 0, DateTimeKind.Utc), entry.LastModifiedUtc);
    }

    [Fact]
    public void ScoreUrlAgainstFixture_PrefersUrlsContainingBothTeams()
    {
        var score = SofaScoreDiscoveryHelper.ScoreUrlAgainstFixture(
            "https://www.sofascore.com/football/match/real-madrid-manchester-city/rsEgb",
            SofaScoreDiscoveryHelper.BuildSlugCandidates("Manchester City"),
            SofaScoreDiscoveryHelper.BuildSlugCandidates("Real Madrid"));

        Assert.True(score > 0);
    }

    [Fact]
    public void ExtractMatchUrlsFromHtml_ReturnsNormalizedRenderedLinks()
    {
        const string html = """
            <html>
            <body>
              <a href="/football/match/real-madrid-manchester-city/rsEgb#id:15631365">Man City - Real Madrid</a>
              <a href="https://www.sofascore.com/football/match/arsenal-chelsea/NR?tab=overview">Arsenal - Chelsea</a>
              <a href="/basketball/match/lakers-celtics/ABC">Ignore</a>
            </body>
            </html>
            """;

        var urls = SofaScoreDiscoveryHelper.ExtractMatchUrlsFromHtml(html, "https://www.sofascore.com");

        Assert.Equal(2, urls.Count);
        Assert.Contains("https://www.sofascore.com/football/match/real-madrid-manchester-city/rsEgb", urls);
        Assert.Contains("https://www.sofascore.com/football/match/arsenal-chelsea/NR", urls);
    }

    [Fact]
    public void TryParse_FinishedExtraTimePage_UsesRegularTimeScoreForSettlement()
    {
        const string html = """
            <html>
            <body>
            <h1>Madagascar is going head to head with Sudan starting on 18 Mar 2026 at 19:00 UTC</h1>
            <p>The match is a part of the Africa Cup of Nations Qualification.</p>
            <div>After extra time</div>
            <div>ET 1 - 0</div>
            <div>FT 0 - 0</div>
            <div>HT 0 - 0</div>
            </body>
            </html>
            """;

        var parsed = SofaScoreEventPageParser.TryParse(
            html,
            "https://www.sofascore.com/football/match/madagascar-sudan/JUbsKkd",
            out var score);

        Assert.True(parsed);
        Assert.Equal("Madagascar", score.HomeTeam);
        Assert.Equal("Sudan", score.AwayTeam);
        Assert.Equal("Africa Cup of Nations Qualification", score.League);
        Assert.Equal("After extra time", score.StatusText);
        Assert.Equal("0:0", score.Score);
        Assert.Equal("0:0", score.RegularTimeScore);
        Assert.Equal("1:0", score.ExtraTimeScore);
        Assert.False(score.IsLive);
    }

    [Fact]
    public void TryParse_LivePage_MarksScoreAsLive()
    {
        const string html = """
            <html>
            <body>
            <h1>Arsenal is going head to head with Chelsea starting on 18 Mar 2026 at 20:00 UTC</h1>
            <p>The match is a part of the Premier League.</p>
            <div>67'</div>
            <div>2 - 1</div>
            </body>
            </html>
            """;

        var parsed = SofaScoreEventPageParser.TryParse(
            html,
            "https://www.sofascore.com/football/match/arsenal-chelsea/NR",
            out var score);

        Assert.True(parsed);
        Assert.True(score.IsLive);
        Assert.Equal("2:1", score.Score);
        Assert.Equal("67'", score.StatusText);
    }
}
