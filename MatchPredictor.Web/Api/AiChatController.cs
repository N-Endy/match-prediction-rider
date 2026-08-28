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
}

public class ChatRequest
{
    public string Message { get; set; } = string.Empty;
}
