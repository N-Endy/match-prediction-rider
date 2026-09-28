using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Web.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;

namespace MatchPredictor.Web.Api;

[ApiController]
[Route("api/ai")]
[EnableRateLimiting(RateLimitPolicies.AiChat)]
public class AiChatController : ControllerBase
{
    private readonly IAiAdvisorService _aiService;
    private readonly IAiChatAuthTicketService _authTicketService;
    private readonly IUserTrackingService _userTrackingService;
    private readonly ILogger<AiChatController> _logger;

    public AiChatController(
        IAiAdvisorService aiService,
        IAiChatAuthTicketService authTicketService,
        IUserTrackingService userTrackingService,
        ILogger<AiChatController> logger)
    {
        _aiService = aiService;
        _authTicketService = authTicketService;
        _userTrackingService = userTrackingService;
        _logger = logger;
    }

    [HttpPost("chat")]
    public async Task<IActionResult> Chat([FromBody] ChatRequest request, CancellationToken ct)
    {
        string sessionId;
        if (_authTicketService.IsLoginRequired)
        {
            if (!_authTicketService.TryValidate(HttpContext, out var authenticatedSessionId))
            {
                return Unauthorized(new { message = "Unauthorized. Please authenticate on the AI Chat page." });
            }

            sessionId = authenticatedSessionId!;
        }
        else
        {
            sessionId = _authTicketService.EnsureSession(HttpContext);
        }

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest(new { message = "Please enter a message." });
        }

        try
        {
            var response = await _aiService.GetAdviceAsync(request.Message, sessionId, ct);

            await _userTrackingService.TrackEventAsync(
                HttpContext,
                "ai_chat_request",
                "/aichat",
                new Dictionary<string, string?>
                {
                    ["promptLength"] = request.Message.Length.ToString(),
                    ["actionCount"] = response.Actions.Count.ToString(),
                    ["showBookAll"] = response.ShowBookAll.ToString()
                },
                ct);

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI Chat request failed.");
            return StatusCode(500, new { message = "AI Chat is temporarily unavailable. Please try again in a moment." });
        }
    }

    [HttpPost("chat/stream")]
    public async Task Stream([FromBody] ChatRequest request, CancellationToken ct)
    {
        string sessionId;
        if (_authTicketService.IsLoginRequired)
        {
            if (!_authTicketService.TryValidate(HttpContext, out var authenticatedSessionId))
            {
                Response.StatusCode = StatusCodes.Status401Unauthorized;
                Response.ContentType = "application/json";
                await Response.WriteAsync("{\"message\":\"Unauthorized. Please authenticate on the AI Chat page.\"}", ct);
                return;
            }

            sessionId = authenticatedSessionId!;
        }
        else
        {
            sessionId = _authTicketService.EnsureSession(HttpContext);
        }

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            Response.ContentType = "application/json";
            await Response.WriteAsync("{\"message\":\"Please enter a message.\"}", ct);
            return;
        }

        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        try
        {
            await foreach (var chunk in _aiService.StreamAdviceAsync(request.Message, sessionId, ct))
            {
                var json = System.Text.Json.JsonSerializer.Serialize(chunk);
                await Response.WriteAsync($"data: {json}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Client closed stream connection
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI Chat streaming request failed.");
            var errorChunk = new AiChatStreamChunk { EventType = "text", Content = " [AI stream temporarily interrupted. Please refresh or try again.]" };
            await Response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(errorChunk)}\n\n", ct);
            await Response.WriteAsync("data: {\"EventType\":\"done\"}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }
    }
}

public class ChatRequest
{
    public string Message { get; set; } = string.Empty;
}
