using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace MatchPredictor.Web.Api;

[ApiController]
[Route("api/ai")]
public class AiChatController : ControllerBase
{
    private readonly IAiAdvisorService _aiAdvisorService;
    private readonly IAiChatAuthTicketService _authTicketService;
    private readonly IConfiguration _configuration;

    public AiChatController(
        IAiAdvisorService aiAdvisorService,
        IAiChatAuthTicketService authTicketService,
        IConfiguration configuration)
    {
        _aiAdvisorService = aiAdvisorService;
        _authTicketService = authTicketService;
        _configuration = configuration;
    }

    [EnableRateLimiting(RateLimitPolicies.AiChat)]
    [HttpPost("chat")]
    public async Task<IActionResult> Chat([FromBody] AiChatRequest? request, CancellationToken ct)
    {
        var message = request?.Message?.Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            return BadRequest(new { message = "Enter a tennis question before sending." });
        }

        string sessionId;
        if (IsAuthenticationRequired())
        {
            if (!_authTicketService.TryValidate(HttpContext, out var validatedSessionId) ||
                string.IsNullOrWhiteSpace(validatedSessionId))
            {
                return Unauthorized(new { message = "AI chat session expired. Reload the page and sign in again." });
            }

            sessionId = validatedSessionId;
        }
        else
        {
            if (!_authTicketService.TryValidate(HttpContext, out var validatedSessionId) ||
                string.IsNullOrWhiteSpace(validatedSessionId))
            {
                sessionId = _authTicketService.SignIn(HttpContext);
            }
            else
            {
                sessionId = validatedSessionId;
            }
        }

        var response = await _aiAdvisorService.GetAdviceAsync(message, sessionId, ct);
        return Ok(response);
    }

    private bool IsAuthenticationRequired()
    {
        return !string.IsNullOrWhiteSpace(_configuration["AiChatPassword"]);
    }

    public sealed class AiChatRequest
    {
        public string Message { get; set; } = string.Empty;
    }
}
