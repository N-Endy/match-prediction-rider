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
            // Map web DTOs to domain DTOs and forward history
            var domainHistory = request.History?
                .Select(h => new ChatHistoryItem { Role = h.Role, Content = h.Content })
                .ToList();

            var response = await _aiService.GetAdviceAsync(request.Message, domainHistory, ct);
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
    public List<ChatHistoryEntry>? History { get; set; }
}

public class ChatHistoryEntry
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}
