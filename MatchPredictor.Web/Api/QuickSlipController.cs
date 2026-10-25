using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace MatchPredictor.Web.Api;

[ApiController]
[Route("api/slip")]
[EnableRateLimiting(RateLimitPolicies.AiChat)]
public sealed class QuickSlipController : ControllerBase
{
    private readonly IQuickSlipService _quickSlipService;
    private readonly IUserTrackingService _userTrackingService;
    private readonly ILogger<QuickSlipController> _logger;

    public QuickSlipController(
        IQuickSlipService quickSlipService,
        IUserTrackingService userTrackingService,
        ILogger<QuickSlipController> logger)
    {
        _quickSlipService = quickSlipService;
        _userTrackingService = userTrackingService;
        _logger = logger;
    }

    [HttpPost("ai-parse")]
    public async Task<IActionResult> ParseAndStage([FromBody] QuickSlipParseRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Input))
        {
            return BadRequest(new QuickSlipParseResultDto
            {
                Success = false,
                Summary = "Please enter what you want to build into your slip."
            });
        }

        try
        {
            var result = await _quickSlipService.ParseAndStageAsync(request, ct);

            await _userTrackingService.TrackEventAsync(
                HttpContext,
                "quick_slip_ai_parse",
                "/api/slip/ai-parse",
                new Dictionary<string, string?>
                {
                    ["success"] = result.Success.ToString(),
                    ["staged_legs"] = result.StagedPicks.Count.ToString(),
                    ["used_fallback"] = result.UsedFallback.ToString()
                },
                ct);

            return Ok(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse quick slip input: {Input}", request.Input);
            return StatusCode(500, new QuickSlipParseResultDto
            {
                Success = false,
                Summary = "An unexpected error occurred while preparing your slip. Please try again."
            });
        }
    }
}
