using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using Microsoft.AspNetCore.Mvc;

namespace MatchPredictor.Web.Api;

[ApiController]
[Route("api/ai")]
public class AiChatController : ControllerBase
{
    private readonly IAiAdvisorService _aiService;

    public AiChatController(IAiAdvisorService aiService)
    {
        _aiService = aiService;
    }

    [HttpPost("chat")]
    public async Task<IActionResult> Chat([FromBody] ChatRequest request, CancellationToken ct)
    {
        if (!Request.Cookies.ContainsKey("MP_AI_AUTH"))
        {
            return Unauthorized(new { response = "Unauthorized. Please authenticate on the AI Chat page." });
        }

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest(new { response = "Please enter a message." });
        }

        try
        {
            var response = await _aiService.GetAdviceAsync(request.Message, request.History, ct);
            return Ok(new { response });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { response = $"AI service error: {ex.Message}" });
        }
    }
}

public class ChatRequest
{
    public string Message { get; set; } = string.Empty;
    public List<ChatHistoryItem>? History { get; set; }
}
