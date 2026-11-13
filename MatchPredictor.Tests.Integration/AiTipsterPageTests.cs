using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Web.Pages;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class AiTipsterPageTests
{
    private sealed class StubOperationsTipsterService : IOperationsTipsterService
    {
        public bool ThrowException { get; set; }
        public OperationsDossierDto DossierToReturn { get; set; } = new();

        public Task<OperationsDossierDto> GetOperationsDossierAsync(DateOnly? targetDate = null, CancellationToken ct = default)
        {
            if (ThrowException)
            {
                throw new InvalidOperationException("Simulated tipster failure");
            }

            return Task.FromResult(DossierToReturn);
        }

        public Task<OperationsDossierDto> RefreshOperationsDossierAsync(DateOnly? targetDate = null, CancellationToken ct = default)
        {
            return Task.FromResult(DossierToReturn);
        }
    }

    [Fact]
    public async Task OnGetAsync_PopulatesPublicDossier_Successfully()
    {
        var sampleDossier = new OperationsDossierDto
        {
            SlateDate = DateOnly.FromDateTime(DateTime.UtcNow),
            SlateGrade = "GRADE A — PRIME BOARD (+EV DENSE)",
            TotalFixturesAnalyzed = 32,
            PublishedPicksCount = 24,
            TotalDailyRiskBudgetPercent = 5.0m,
            EngineMode = "Autonomous Reasoning Agent",
            Tickets =
            [
                new OperationsPillarTicketDto
                {
                    PillarKind = OperationsPillarKind.Compounder,
                    Title = "The Capital Compounder",
                    CombinedOdds = 1.90m,
                    AllocationPercent = 60m,
                    RecommendedUnits = 3.0m,
                    BookingCode = "PUB-12345"
                }
            ]
        };

        var stubService = new StubOperationsTipsterService { DossierToReturn = sampleDossier };
        var pageModel = new AiTipsterModel(stubService, NullLogger<AiTipsterModel>.Instance);

        await pageModel.OnGetAsync();

        Assert.NotNull(pageModel.Dossier);
        Assert.Equal("GRADE A — PRIME BOARD (+EV DENSE)", pageModel.Dossier.SlateGrade);
        Assert.Equal("Autonomous Reasoning Agent", pageModel.Dossier.EngineMode);
        Assert.Single(pageModel.Dossier.Tickets);
        Assert.Equal("PUB-12345", pageModel.Dossier.Tickets[0].BookingCode);
    }

    [Fact]
    public async Task OnGetAsync_WhenServiceThrows_HandlesGracefullyWithoutCrashing()
    {
        var stubService = new StubOperationsTipsterService { ThrowException = true };
        var pageModel = new AiTipsterModel(stubService, NullLogger<AiTipsterModel>.Instance);

        await pageModel.OnGetAsync();

        Assert.Null(pageModel.Dossier);
    }
}
