using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Web.Pages.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class OperationsPageTests
{
    private sealed class StubOperationsTipsterService : IOperationsTipsterService
    {
        public bool RefreshCalled { get; private set; }
        public OperationsDossierDto DossierToReturn { get; set; } = new();

        public Task<OperationsDossierDto> GetOperationsDossierAsync(DateOnly? targetDate = null, CancellationToken ct = default)
        {
            return Task.FromResult(DossierToReturn);
        }

        public Task<OperationsDossierDto> RefreshOperationsDossierAsync(DateOnly? targetDate = null, CancellationToken ct = default)
        {
            RefreshCalled = true;
            return Task.FromResult(DossierToReturn);
        }
    }

    private sealed class DummyTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    [Fact]
    public async Task OnGetAsync_PopulatesDossierFromTipsterService()
    {
        var stubService = new StubOperationsTipsterService
        {
            DossierToReturn = new OperationsDossierDto
            {
                SlateDate = DateOnly.FromDateTime(DateTime.UtcNow),
                SlateGrade = "GRADE A — PRIME BOARD (+EV DENSE)",
                TotalFixturesAnalyzed = 28,
                PublishedPicksCount = 22,
                TotalDailyRiskBudgetPercent = 5.0m,
                Tickets =
                [
                    new OperationsPillarTicketDto
                    {
                        PillarKind = OperationsPillarKind.Compounder,
                        Title = "The Capital Compounder",
                        CombinedOdds = 1.95m,
                        AllocationPercent = 60m,
                        RecommendedUnits = 3.0m,
                        BookingCode = "OP-12345"
                    }
                ]
            }
        };

        var pageModel = new OperationsModel(stubService, NullLogger<OperationsModel>.Instance);

        await pageModel.OnGetAsync();

        Assert.NotNull(pageModel.Dossier);
        Assert.Equal("GRADE A — PRIME BOARD (+EV DENSE)", pageModel.Dossier.SlateGrade);
        Assert.Single(pageModel.Dossier.Tickets);
        Assert.Equal("OP-12345", pageModel.Dossier.Tickets[0].BookingCode);
    }

    [Fact]
    public async Task OnPostRefreshAsync_InvokesRefreshAndRedirectsToOperations()
    {
        var stubService = new StubOperationsTipsterService
        {
            DossierToReturn = new OperationsDossierDto
            {
                SlateDate = DateOnly.FromDateTime(DateTime.UtcNow),
                SlateGrade = "GRADE B",
                TotalFixturesAnalyzed = 14,
                PublishedPicksCount = 10,
                Tickets = []
            }
        };

        var pageModel = new OperationsModel(stubService, NullLogger<OperationsModel>.Instance);

        var httpContext = new DefaultHttpContext();
        pageModel.TempData = new TempDataDictionary(httpContext, new DummyTempDataProvider());

        var result = await pageModel.OnPostRefreshAsync();

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Admin/Operations", redirect.PageName);
        Assert.True(pageModel.StatusAlertSuccess);
        Assert.Contains("re-analyzed", pageModel.StatusAlertMessage);
        Assert.True(stubService.RefreshCalled);
    }
}
