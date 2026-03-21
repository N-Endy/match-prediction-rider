using Microsoft.AspNetCore.Mvc;

namespace MatchPredictor.Web.Api;

[ApiController]
[Route("api/tracking")]
public class TrackingController : ControllerBase
{
    [HttpPost("event")]
    public IActionResult TrackEvent()
    {
        return NotFound(new
        {
            message = "Tracking endpoints are disabled in TennisPredictor v1."
        });
    }
}
