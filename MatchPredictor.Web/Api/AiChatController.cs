using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace MatchPredictor.Web.Api;

[ApiController]
[Route("api/ai")]
public class AiChatController : ControllerBase
{
    private readonly IAiAdvisorService _aiService;
    private readonly IUserTrackingService _userTrackingService;
    private readonly ILogger<AiChatController> _logger;
    private const string AuthCookieName = "MP_AI_AUTH";
    private const string SessionCookieName = "MP_AI_CHAT_SESSION";

    public AiChatController(IAiAdvisorService aiService, IUserTrackingService userTrackingService, ILogger<AiChatController> logger)
    {
        _aiService = aiService;
        _userTrackingService = userTrackingService;
        _logger = logger;
    }

    [HttpPost("chat")]
    public async Task<IActionResult> Chat([FromBody] ChatRequest request, CancellationToken ct)
    {
        if (!Request.Cookies.ContainsKey(AuthCookieName))
        {
            return Unauthorized(new { message = "Unauthorized. Please authenticate on the AI Chat page." });
        }

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest(new { message = "Please enter a message." });
        }

        try
        {
            var sessionId = Request.Cookies.TryGetValue(SessionCookieName, out var existingSessionId) &&
                            !string.IsNullOrWhiteSpace(existingSessionId)
                ? existingSessionId
                : Guid.NewGuid().ToString("N");

            if (!Request.Cookies.ContainsKey(SessionCookieName))
            {
                Response.Cookies.Append(SessionCookieName, sessionId, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = Request.IsHttps,
                    SameSite = SameSiteMode.Strict
                });
            }

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
