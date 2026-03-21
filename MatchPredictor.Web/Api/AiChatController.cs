using Microsoft.AspNetCore.Mvc;

namespace MatchPredictor.Web.Api;

[ApiController]
[Route("api/ai")]
public class AiChatController : ControllerBase
{
    [HttpPost("chat")]
    public IActionResult Chat()
    {
        return NotFound(new
        {
            message = "AI chat is not available in TennisPredictor v1."
        });
    }
}
