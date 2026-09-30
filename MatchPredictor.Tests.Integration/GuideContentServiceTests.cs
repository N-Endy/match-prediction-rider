using MatchPredictor.Web.Services;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class GuideContentServiceTests
{
    private readonly GuideContentService _service = new();

    [Fact]
    public void GetAllGuides_ReturnsAllFiveResearchArticles()
    {
        var guides = _service.GetAllGuides();

        Assert.NotNull(guides);
        Assert.Equal(5, guides.Count);
        Assert.All(guides, g =>
        {
            Assert.False(string.IsNullOrWhiteSpace(g.Slug));
            Assert.False(string.IsNullOrWhiteSpace(g.Title));
            Assert.False(string.IsNullOrWhiteSpace(g.Summary));
            Assert.False(string.IsNullOrWhiteSpace(g.Category));
            Assert.False(string.IsNullOrWhiteSpace(g.ContentHtml));
            Assert.Contains("<h2", g.ContentHtml);
        });
    }

    [Theory]
    [InlineData("the-math-of-football-predictions-poisson-and-dixon-coles")]
    [InlineData("understanding-market-odds-margin-removal-and-shins-method")]
    [InlineData("probability-calibration-and-brier-score-explained")]
    [InlineData("both-teams-to-score-btts-statistical-modeling")]
    [InlineData("managing-variance-in-sports-analytics")]
    public void GetGuideBySlug_ReturnsSpecificGuideWithSubstantiveContent(string slug)
    {
        var guide = _service.GetGuideBySlug(slug);

        Assert.NotNull(guide);
        Assert.Equal(slug, guide.Slug);
        Assert.True(guide.ContentHtml.Length > 500, "Guide should contain substantive educational text");
    }

    [Fact]
    public void GetGuideBySlug_CaseInsensitiveMatch()
    {
        var guide = _service.GetGuideBySlug("THE-MATH-OF-FOOTBALL-PREDICTIONS-POISSON-AND-DIXON-COLES");

        Assert.NotNull(guide);
        Assert.Equal("the-math-of-football-predictions-poisson-and-dixon-coles", guide.Slug);
    }

    [Fact]
    public void GetGuideBySlug_ReturnsNullForUnknownSlug()
    {
        var guide = _service.GetGuideBySlug("unknown-non-existent-guide");

        Assert.Null(guide);
    }
}
