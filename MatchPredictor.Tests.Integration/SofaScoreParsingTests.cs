using MatchPredictor.Infrastructure.Services;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class SofaScoreParsingTests
{
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
