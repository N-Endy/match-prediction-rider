using System.Text.Json;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Web.Api;
using MatchPredictor.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class AiChatControllerTests
{
    [Fact]
    public async Task Chat_WithoutAuthCookie_ReturnsUnauthorized()
    {
        var controller = new AiChatController(
            new FakeAiAdvisorService(),
            new FakeAiChatAuthTicketService(),
            new FakeUserTrackingService(),
            NullLogger<AiChatController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var result = await controller.Chat(new ChatRequest { Message = "Hello" }, CancellationToken.None);

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, unauthorized.StatusCode);
        Assert.Contains("Unauthorized", JsonSerializer.Serialize(unauthorized.Value));
    }

    [Fact]
    public async Task Chat_WhenServiceThrows_ReturnsGeneric500WithoutInternalDetails()
    {
        var httpContext = new DefaultHttpContext();

        var controller = new AiChatController(
            new FakeAiAdvisorService { ExceptionToThrow = new InvalidOperationException("sensitive internals") },
            new FakeAiChatAuthTicketService { IsValid = true, SessionId = "session-123" },
            new FakeUserTrackingService(),
            NullLogger<AiChatController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            }
        };

        var result = await controller.Chat(new ChatRequest { Message = "Hello" }, CancellationToken.None);

        var failure = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, failure.StatusCode);

        var payload = JsonSerializer.Serialize(failure.Value);
        Assert.Contains("temporarily unavailable", payload);
        Assert.DoesNotContain("sensitive internals", payload);
    }

    [Fact]
    public async Task Chat_WithTamperedAuthTicket_ReturnsUnauthorized()
    {
        var controller = new AiChatController(
            new FakeAiAdvisorService(),
            new FakeAiChatAuthTicketService { IsValid = false },
            new FakeUserTrackingService(),
            NullLogger<AiChatController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var result = await controller.Chat(new ChatRequest { Message = "Hello" }, CancellationToken.None);

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, unauthorized.StatusCode);
    }

    private sealed class FakeAiAdvisorService : IAiAdvisorService
    {
        public Exception? ExceptionToThrow { get; init; }

        public Task<AiChatResponse> GetAdviceAsync(string userPrompt, string sessionId, CancellationToken ct = default)
        {
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return Task.FromResult(new AiChatResponse
            {
                Message = "ok"
            });
        }

        public Task<string> AnalyzeValueBetsAsync(string payload, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);

        public Task<IReadOnlyList<BetslipDrawPickSelection>> SelectBestDrawPicksAsync(
            IReadOnlyList<BetslipDrawPickRequest> candidates,
            int count = 5,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BetslipDrawPickSelection>>(
                candidates.Take(count).Select(c => new BetslipDrawPickSelection { PredictionId = c.PredictionId }).ToList());

        public Task<BankerPickResult> SelectBankerPicksAsync(
            IReadOnlyList<BankerPickRequest> candidates,
            double minOdds,
            double maxOdds,
            CancellationToken ct = default) =>
            Task.FromResult(new BankerPickResult());

        public Task<LadderRankResult> RankLadderCandidatesAsync(
            IReadOnlyList<LadderRankRequest> candidates,
            CancellationToken ct = default) =>
            Task.FromResult(new LadderRankResult());

        public Task<BetslipScreenResult> ScreenBetslipCandidatesAsync(
            IReadOnlyList<BetslipScreenRequest> candidates,
            CancellationToken ct = default) =>
            Task.FromResult(new BetslipScreenResult());

        public Task<LadderComposeResult> ComposeLadderSlipsAsync(
            IReadOnlyList<LadderRankRequest> candidates,
            IReadOnlyList<LadderComposeBandRequest> bands,
            CancellationToken ct = default) =>
            Task.FromResult(new LadderComposeResult());
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

    private sealed class FakeAiChatAuthTicketService : IAiChatAuthTicketService
    {
        public bool IsValid { get; init; }
        public string? SessionId { get; init; }

        public bool TryValidate(HttpContext httpContext, out string? sessionId)
        {
            sessionId = IsValid ? SessionId ?? "session-1" : null;
            return IsValid;
        }

        public bool IsAuthenticated(HttpContext httpContext) => IsValid;

        public string SignIn(HttpContext httpContext) => SessionId ?? "session-1";

        public void SignOut(HttpContext httpContext)
        {
        }
    }
}
