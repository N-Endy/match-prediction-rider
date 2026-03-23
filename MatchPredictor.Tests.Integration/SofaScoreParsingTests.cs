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

    [Fact]
    public void ParseEntries_ParsesMultipleLiveHomepageRowsFromRealListingHtml()
    {
        const string html = """
            <div class="pb_sm">
                <div class="d_flex ai_center ps_xl cursor_pointer">
                    <a href="/football/tournament/armenia/first-league/672" style="max-width: fit-content;">
                        <div class="w_xl h_xl"><img alt="Armenian 1st League" class="w_xl h_xl obj-f_contain unique-tournament-image" src="https://img.sofascore.com/api/v1/unique-tournament/672/image"></div>
                    </a>
                    <div class="d_flex flex-d_column jc_center flex_[1_0_0px] mx_lg h_4xl ov_hidden">
                        <a href="/football/tournament/armenia/first-league/672" style="max-width: fit-content;"><bdi class="textStyle_display.micro c_neutrals.nLv1 hover:c_primary.default hover:td_underline trunc_true d_block">First League</bdi></a>
                        <a href="/football/armenia" style="max-width: fit-content;">
                            <div class="d_flex ai_center gap_xs mt_2xs w_[fit-content]"><img alt="Armenia" class="" src="https://img.sofascore.com/api/v1/category/296/image"><bdi class="textStyle_assistive.default c_neutrals.nLv3 hover:c_primary.default hover:td_underline trunc_true">Armenia</bdi></div>
                        </a>
                    </div>
                    <div class="d_flex ai_center gap_0"><span class="textStyle_body.small c_status.live">2</span><span class="textStyle_body.small c_neutrals.nLv3 me_sm">/2</span></div>
                </div>
                <a data-id="15673600" class="event-hl-15673600 d_block br_2xs hover:bg_surface.s2 active:bg_surface.s0" href="/football/match/fc-hayq-urartu-ii/DpFcsBBTi#id:15673600">
                    <div class="d_flex ai_center h_4xl c_neutrals.nLv1 pos_relative">
                        <div class="pos_absolute top_2xs inset-s_xs w_[calc(100%_-_8px)] h_[44px] ov_hidden">
                            <div class="pos_relative w_100% h_[44px] bg_status.live/10 trf_translate(-100%)"></div>
                        </div>
                        <div title="1ST" class="ta_center mx_sm flex-sh_0 flex-b_5xl">
                            <bdi class="textStyle_body.small c_neutrals.nLv3">12:30</bdi>
                            <div class="pos_relative d_flex flex-d_column min-w_lg h_[17px] ai_flex-end bd-w_1px border-style_solid bd-c_[transparent] ov_hidden" style="align-items: center;">
                                <span class="textStyle_body.medium c_neutrals.nLv1 pos_relative min-w_lg ta_center score"><bdi class="textStyle_body.small c_neutrals.nLv1 ta_center w_5xl d_block trunc_true">26'</bdi></span>
                                <span class="textStyle_body.medium c_neutrals.nLv1 pos_relative min-w_lg ta_center score"></span>
                            </div>
                        </div>
                        <div class="me_sm h_[36px] flex_[0_0_1px] bg_neutrals.nLv4"></div>
                        <div title="FC Hayq Urartu II live score" class="ov_hidden min-w_[0px]" style="flex: 1 1 140px;">
                            <div class="d_flex ai_center mb_2xs">
                                <div class="w_lg h_lg me_sm"><img alt="FC Hayq" class="" src="https://img.sofascore.com/api/v1/team/1108826/image/small"></div>
                                <bdi class="textStyle_body.medium c_neutrals.nLv1 trunc_true">FC Hayq</bdi>
                            </div>
                            <div class="d_flex ai_center">
                                <div class="w_lg h_lg me_sm"><img alt="Urartu II" class="" src="https://img.sofascore.com/api/v1/team/325778/image/small"></div>
                                <bdi class="textStyle_body.medium c_neutrals.nLv1 trunc_true">Urartu II</bdi>
                            </div>
                        </div>
                        <div style="min-width: 132px;"></div>
                        <div class="d_flex flex-d_column ai_flex-end jc_flex-end w_[38px] me_sm c_status.live pos_relative">
                            <div class="pos_relative d_flex flex-d_column min-w_lg h_[17px] ai_flex-end bd-w_1px border-style_solid bd-c_[transparent] ov_hidden">
                                <span class="textStyle_body.medium c_neutrals.nLv1 pos_relative min-w_lg ta_end score">0</span>
                                <span class="textStyle_body.medium c_neutrals.nLv1 pos_relative min-w_lg ta_end score">0</span>
                            </div>
                            <div class="pos_relative d_flex flex-d_column min-w_lg h_[17px] ai_flex-end bd-w_1px border-style_solid bd-c_[transparent] ov_hidden">
                                <span class="textStyle_body.medium c_neutrals.nLv1 pos_relative min-w_lg ta_end score">0</span>
                                <span class="textStyle_body.medium c_neutrals.nLv1 pos_relative min-w_lg ta_end score">0</span>
                            </div>
                        </div>
                    </div>
                </a>
                <a data-id="15673594" class="event-hl-15673594 d_block br_2xs hover:bg_surface.s2 active:bg_surface.s0" href="/football/match/lernayin-artsakh-fc-pyunik-ii/byosHPHc#id:15673594">
                    <div class="d_flex ai_center h_4xl c_neutrals.nLv1 pos_relative">
                        <div class="pos_absolute top_2xs inset-s_xs w_[calc(100%_-_8px)] h_[44px] ov_hidden">
                            <div class="pos_relative w_100% h_[44px] bg_status.live/10 trf_translate(-100%)"></div>
                        </div>
                        <div title="1ST" class="ta_center mx_sm flex-sh_0 flex-b_5xl">
                            <bdi class="textStyle_body.small c_neutrals.nLv3">12:30</bdi>
                            <div class="pos_relative d_flex flex-d_column min-w_lg h_[17px] ai_flex-end bd-w_1px border-style_solid bd-c_[transparent] ov_hidden" style="align-items: center;">
                                <span class="textStyle_body.medium c_neutrals.nLv1 pos_relative min-w_lg ta_center score"><bdi class="textStyle_body.small c_neutrals.nLv1 ta_center w_5xl d_block trunc_true">25'</bdi></span>
                                <span class="textStyle_body.medium c_neutrals.nLv1 pos_relative min-w_lg ta_center score"></span>
                            </div>
                        </div>
                        <div class="me_sm h_[36px] flex_[0_0_1px] bg_neutrals.nLv4"></div>
                        <div title="Pyunik II Lernayin Artsakh FC live score" class="ov_hidden min-w_[0px]" style="flex: 1 1 140px;">
                            <div class="d_flex ai_center mb_2xs">
                                <div class="w_lg h_lg me_sm"><img alt="Pyunik II" class="" src="https://img.sofascore.com/api/v1/team/36151/image/small"></div>
                                <bdi class="textStyle_body.medium c_neutrals.nLv1 trunc_true">Pyunik II</bdi>
                            </div>
                            <div class="d_flex ai_center">
                                <div class="w_lg h_lg me_sm"><img alt="Lernayin Artsakh FC" class="" src="https://img.sofascore.com/api/v1/team/332032/image/small"></div>
                                <bdi class="textStyle_body.medium c_neutrals.nLv1 trunc_true">Lernayin Artsakh </bdi>
                            </div>
                        </div>
                        <div style="min-width: 132px;"></div>
                        <div class="d_flex flex-d_column ai_flex-end jc_flex-end w_[38px] me_sm c_status.live pos_relative">
                            <div class="pos_relative d_flex flex-d_column min-w_lg h_[17px] ai_flex-end bd-w_1px border-style_solid bd-c_[transparent] ov_hidden">
                                <span class="textStyle_body.medium c_neutrals.nLv1 pos_relative min-w_lg ta_end score">0</span>
                                <span class="textStyle_body.medium c_neutrals.nLv1 pos_relative min-w_lg ta_end score">0</span>
                            </div>
                            <div class="pos_relative d_flex flex-d_column min-w_lg h_[17px] ai_flex-end bd-w_1px border-style_solid bd-c_[transparent] ov_hidden">
                                <span class="textStyle_body.medium c_neutrals.nLv1 pos_relative min-w_lg ta_end score">0</span>
                                <span class="textStyle_body.medium c_neutrals.nLv1 pos_relative min-w_lg ta_end score">0</span>
                            </div>
                        </div>
                    </div>
                </a>
            </div>
            """;

        var entries = SofaScoreListingPageParser.ParseEntries(html, "https://www.sofascore.com");

        Assert.Equal(2, entries.Count);

        Assert.Equal("Armenia - First League", entries[0].League);
        Assert.Equal("FC Hayq", entries[0].HomeTeam);
        Assert.Equal("Urartu II", entries[0].AwayTeam);
        Assert.Equal("0:0", entries[0].Score);
        Assert.Equal("26'", entries[0].StatusText);
        Assert.True(entries[0].IsLive);
        Assert.Equal(new TimeOnly(12, 30), entries[0].KickoffLocalTime);
        Assert.Equal("https://www.sofascore.com/football/match/fc-hayq-urartu-ii/DpFcsBBTi", entries[0].EventUrl);

        Assert.Equal("Pyunik II", entries[1].HomeTeam);
        Assert.Equal("Lernayin Artsakh", entries[1].AwayTeam);
        Assert.Equal("0:0", entries[1].Score);
        Assert.Equal("25'", entries[1].StatusText);
        Assert.True(entries[1].IsLive);
        Assert.Equal("https://www.sofascore.com/football/match/lernayin-artsakh-fc-pyunik-ii/byosHPHc", entries[1].EventUrl);
    }

    [Fact]
    public void ParseEntries_ParsesRenderedTennisDomRowsFromBrowserSnapshot()
    {
        var entries = SofaScoreBrowserDomParser.ParseEntries(
            [
                new SofaScoreRenderedDomCandidate
                {
                    Href = "/tennis/match/jiri-lehecka-ethan-quinn/abc123",
                    Text = """
                        18:00
                        Ethan Quinn
                        Jiri Lehecka
                        0
                        2
                        FT
                        """,
                    SectionText = """
                        ATP Miami
                        18:00
                        Ethan Quinn
                        Jiri Lehecka
                        0
                        2
                        FT
                        """
                },
                new SofaScoreRenderedDomCandidate
                {
                    Href = "/tennis/match/coco-gauff-alycia-parks/def456",
                    Text = """
                        Set 2
                        Alycia Parks
                        Coco Gauff
                        0
                        1
                        """,
                    SectionText = """
                        WTA Miami
                        Set 2
                        Alycia Parks
                        Coco Gauff
                        0
                        1
                        """
                }
            ],
            "https://www.sofascore.com");

        Assert.Equal(2, entries.Count);

        Assert.Equal("ATP Miami", entries[0].League);
        Assert.Equal("Ethan Quinn", entries[0].HomeTeam);
        Assert.Equal("Jiri Lehecka", entries[0].AwayTeam);
        Assert.Equal("0:2", entries[0].Score);
        Assert.Equal("FT", entries[0].StatusText);
        Assert.False(entries[0].IsLive);
        Assert.Equal(new TimeOnly(18, 0), entries[0].KickoffLocalTime);
        Assert.Equal("https://www.sofascore.com/tennis/match/jiri-lehecka-ethan-quinn/abc123", entries[0].EventUrl);

        Assert.Equal("WTA Miami", entries[1].League);
        Assert.Equal("Alycia Parks", entries[1].HomeTeam);
        Assert.Equal("Coco Gauff", entries[1].AwayTeam);
        Assert.Equal("0:1", entries[1].Score);
        Assert.Equal("Set 2", entries[1].StatusText);
        Assert.True(entries[1].IsLive);
    }

    [Fact]
    public void ParseEventSummaries_SplitsBrowserTennisLiveAndScheduledPayloads()
    {
        const string liveJson = """
            {
              "events": [
                {
                  "id": 801,
                  "startTimestamp": 1774288800,
                  "status": { "type": "inprogress", "description": "Set 2" },
                  "homeTeam": { "name": "Alycia Parks" },
                  "awayTeam": { "name": "Coco Gauff" },
                  "tournament": {
                    "name": "Miami",
                    "category": { "name": "WTA" }
                  },
                  "homeScore": { "current": 0, "display": 0 },
                  "awayScore": { "current": 1, "display": 1 }
                }
              ]
            }
            """;

        const string scheduledJson = """
            {
              "events": [
                {
                  "id": 802,
                  "startTimestamp": 1774292400,
                  "status": { "type": "finished", "description": "FT" },
                  "homeTeam": { "name": "Ethan Quinn" },
                  "awayTeam": { "name": "Jiri Lehecka" },
                  "tournament": {
                    "name": "Miami",
                    "category": { "name": "ATP" }
                  },
                  "homeScore": { "current": 0, "display": 0 },
                  "awayScore": { "current": 2, "display": 2 }
                }
              ]
            }
            """;

        var responses = new[]
        {
            new SofaScoreBrowserFetchResponse
            {
                RelativePath = "/api/v1/sport/tennis/events/live",
                Ok = true,
                Status = 200,
                Body = liveJson
            },
            new SofaScoreBrowserFetchResponse
            {
                RelativePath = "/api/v1/sport/tennis/scheduled-events/2026-03-23",
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
        Assert.Equal("Alycia Parks", liveEvents[0].HomeTeam);
        Assert.Equal("Coco Gauff", liveEvents[0].AwayTeam);
        Assert.Equal("0:1", liveEvents[0].Score);
        Assert.Equal("Ethan Quinn", scheduledEvents[0].HomeTeam);
        Assert.Equal("Jiri Lehecka", scheduledEvents[0].AwayTeam);
        Assert.Equal("0:2", scheduledEvents[0].Score);
    }
}
