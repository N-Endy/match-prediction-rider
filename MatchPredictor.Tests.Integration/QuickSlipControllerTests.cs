using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Web.Api;
using MatchPredictor.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class QuickSlipControllerTests
{
    [Fact]
    public async Task ParseAndStage_EmptyInput_ReturnsBadRequest()
    {
        var controller = new QuickSlipController(
            new FakeQuickSlipService(),
            new FakeUserTrackingService(),
            NullLogger<QuickSlipController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.ParseAndStage(new QuickSlipParseRequest { Input = "   " }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var dto = Assert.IsType<QuickSlipParseResultDto>(badRequest.Value);
        Assert.False(dto.Success);
    }

    [Fact]
    public async Task ParseAndStage_ValidInput_ReturnsOkWithStagedPicks()
    {
        var fakeService = new FakeQuickSlipService
        {
            MockResult = new QuickSlipParseResultDto
            {
                Success = true,
                Summary = "Staged 3 safe EPL home wins with 3.45 total odds.",
                TotalCombinedOdds = 3.45m,
                AverageModelProbability = 0.75m,
                StagedPicks =
                [
                    new StagedSlipLegDto { PredictionId = 1, HomeTeam = "Arsenal", AwayTeam = "Chelsea", Market = "StraightWin", Prediction = "Arsenal", Odds = 1.75m },
                    new StagedSlipLegDto { PredictionId = 2, HomeTeam = "Liverpool", AwayTeam = "Everton", Market = "StraightWin", Prediction = "Liverpool", Odds = 1.45m }
                ]
            }
        };

        var tracking = new FakeUserTrackingService();

        var controller = new QuickSlipController(
            fakeService,
            tracking,
            NullLogger<QuickSlipController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.ParseAndStage(new QuickSlipParseRequest { Input = "2 safe epl home wins" }, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<QuickSlipParseResultDto>(okResult.Value);
        Assert.True(dto.Success);
        Assert.Equal(2, dto.StagedPicks.Count);
        Assert.Equal(3.45m, dto.TotalCombinedOdds);
    }

    private sealed class FakeQuickSlipService : IQuickSlipService
    {
        public QuickSlipParseResultDto MockResult { get; set; } = new() { Success = true };

        public Task<QuickSlipParseResultDto> ParseAndStageAsync(QuickSlipParseRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(MockResult);
        }
    }

    private sealed class FakeUserTrackingService : IUserTrackingService
    {
        public bool IsEnabled => true;

        public Task EnsureTrackingContextAsync(HttpContext httpContext, CancellationToken ct = default) => Task.CompletedTask;

        public Task TrackPageViewAsync(HttpContext httpContext, CancellationToken ct = default) => Task.CompletedTask;

        public Task TrackEventAsync(
            HttpContext httpContext,
            string eventType,
            string? pagePath = null,
            IReadOnlyDictionary<string, string?>? metadata = null,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken ct = default) =>
            Task.FromResult(new UsageSnapshot());
    }
}
