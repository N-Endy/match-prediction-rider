using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Web.Pages.Predictions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class FilteredPredictionPageModelTests
{
    private sealed class FakePredictionQueries(IReadOnlyList<Prediction> predictions) : IPredictionQueries
    {
        public Task<IReadOnlyList<Prediction>> GetBTTSAsync(DateTime date) => Task.FromResult(predictions);
        public Task<IReadOnlyList<Prediction>> GetOver25Async(DateTime date) => Task.FromResult<IReadOnlyList<Prediction>>([]);
        public Task<IReadOnlyList<Prediction>> GetUnder25Async(DateTime date) => Task.FromResult<IReadOnlyList<Prediction>>([]);
        public Task<IReadOnlyList<Prediction>> GetStraightWinAsync(DateTime date) => Task.FromResult<IReadOnlyList<Prediction>>([]);
        public Task<IReadOnlyList<Prediction>> GetDrawAsync(DateTime date) => Task.FromResult<IReadOnlyList<Prediction>>([]);
        public Task<IReadOnlyList<Prediction>> GetRecentSettledPublishedAsync(int days = 30) => Task.FromResult<IReadOnlyList<Prediction>>([]);
    }

    private static BTTS CreatePage(IReadOnlyList<Prediction> predictions, string outcomeFilter = "all")
    {
        var httpContext = new DefaultHttpContext();
        var modelState = new ModelStateDictionary();
        var actionContext = new ActionContext(httpContext, new RouteData(), new PageActionDescriptor(), modelState);
        var modelMetadataProvider = new EmptyModelMetadataProvider();
        var viewData = new ViewDataDictionary(modelMetadataProvider, modelState);
        var pageContext = new PageContext(actionContext)
        {
            ViewData = viewData
        };

        return new BTTS(new FakePredictionQueries(predictions))
        {
            PageContext = pageContext,
            OutcomeFilter = outcomeFilter
        };
    }

    private static List<Prediction> CreateSampleBttsPredictions()
    {
        return
        [
            new Prediction
            {
                Id = 1,
                HomeTeam = "Arsenal",
                AwayTeam = "Chelsea",
                League = "Premier League",
                PredictionCategory = "BothTeamsScore",
                PredictedOutcome = "BTTS",
                ConfidenceScore = 0.72m
            },
            new Prediction
            {
                Id = 2,
                HomeTeam = "Liverpool",
                AwayTeam = "Everton",
                League = "Premier League",
                PredictionCategory = "BothTeamsScore",
                PredictedOutcome = "BTTS: Yes",
                ConfidenceScore = 0.68m
            },
            new Prediction
            {
                Id = 3,
                HomeTeam = "Juventus",
                AwayTeam = "Monza",
                League = "Serie A",
                PredictionCategory = "BothTeamsScore",
                PredictedOutcome = "No BTTS",
                ConfidenceScore = 0.75m
            },
            new Prediction
            {
                Id = 4,
                HomeTeam = "Atletico Madrid",
                AwayTeam = "Getafe",
                League = "La Liga",
                PredictionCategory = "BothTeamsScore",
                PredictedOutcome = "BTTS: No",
                ConfidenceScore = 0.70m
            }
        ];
    }

    [Fact]
    public async Task BTTSPage_WhenOutcomeFilterIsAll_ReturnsAllPredictions()
    {
        var predictions = CreateSampleBttsPredictions();
        var page = CreatePage(predictions, "all");

        await page.OnGet();

        Assert.Equal(4, page.Matches.Count);
        Assert.Equal(4, page.FilteredMatches.Count);
    }

    [Fact]
    public async Task BTTSPage_WhenOutcomeFilterIsYes_ReturnsOnlyBttsYesPredictions()
    {
        var predictions = CreateSampleBttsPredictions();
        var page = CreatePage(predictions, "yes");

        await page.OnGet();

        Assert.Equal(4, page.Matches.Count);
        Assert.Equal(2, page.FilteredMatches.Count);
        Assert.All(page.FilteredMatches, m =>
        {
            Assert.False(m.PredictedOutcome?.Contains("no", StringComparison.OrdinalIgnoreCase) ?? false);
        });
    }

    [Fact]
    public async Task BTTSPage_WhenOutcomeFilterIsNo_ReturnsOnlyNoBttsPredictions()
    {
        var predictions = CreateSampleBttsPredictions();
        var page = CreatePage(predictions, "no");

        await page.OnGet();

        Assert.Equal(4, page.Matches.Count);
        Assert.Equal(2, page.FilteredMatches.Count);
        Assert.All(page.FilteredMatches, m =>
        {
            Assert.True(m.PredictedOutcome?.Contains("no", StringComparison.OrdinalIgnoreCase) ?? false);
        });
    }
}
