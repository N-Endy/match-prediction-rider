using MatchPredictor.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace MatchPredictor.Web.Api;

[ApiController]
[Route("api/tracking")]
public class TrackingController : ControllerBase
{
    private readonly IUserTrackingService _userTrackingService;

    public TrackingController(IUserTrackingService userTrackingService)
    {
        _userTrackingService = userTrackingService;
    }

    [HttpPost("event")]
    public async Task<IActionResult> TrackEvent([FromBody] TrackEventRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.EventType))
        {
            return BadRequest(new { message = "Event type is required." });
        }

        await _userTrackingService.TrackEventAsync(
            HttpContext,
            request.EventType,
            request.PagePath,
            request.Metadata,
            ct);

        return Ok(new { success = true });
    }
}

public class TrackEventRequest
{
    public string EventType { get; set; } = string.Empty;
    public string? PagePath { get; set; }
    public Dictionary<string, string?>? Metadata { get; set; }
}
