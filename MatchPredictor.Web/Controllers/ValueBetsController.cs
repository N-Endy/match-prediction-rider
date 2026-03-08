using MatchPredictor.Domain.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace MatchPredictor.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ValueBetsController : ControllerBase
{
    private readonly IValueBetsService _valueBetsService;
    private readonly ILogger<ValueBetsController> _logger;

    public ValueBetsController(IValueBetsService valueBetsService, ILogger<ValueBetsController> logger)
    {
        _valueBetsService = valueBetsService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetValueBets(CancellationToken ct)
    {
        try
        {
            var valueBets = await _valueBetsService.GetTopValueBetsAsync(60, ct);
            return Ok(valueBets);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch Value Bets");
            return StatusCode(500, new { message = "An error occurred while fetching value bets." });
        }
    }
}
