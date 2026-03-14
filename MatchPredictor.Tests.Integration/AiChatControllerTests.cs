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
        var controller = new AiChatController(new FakeAiAdvisorService(), new FakeUserTrackingService(), NullLogger<AiChatController>.Instance)
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
        httpContext.Request.Headers.Cookie = "MP_AI_AUTH=ok";

        var controller = new AiChatController(
            new FakeAiAdvisorService { ExceptionToThrow = new InvalidOperationException("sensitive internals") },
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
    }

    private sealed class FakeUserTrackingService : IUserTrackingService
    {
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
