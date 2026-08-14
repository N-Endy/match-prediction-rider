using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using Microsoft.AspNetCore.Mvc;

namespace MatchPredictor.Web.Api;

[ApiController]
[Route("admin/api/score-link")]
public sealed class AdminScoreLinkController : ControllerBase
{
    private readonly IManualScoreLinkService _manualScoreLinkService;

    public AdminScoreLinkController(IManualScoreLinkService manualScoreLinkService)
    {
        _manualScoreLinkService = manualScoreLinkService;
    }

    [HttpGet("hints")]
    public async Task<ActionResult<IReadOnlyDictionary<int, ScoreNearMissHintSet>>> GetHints(
        [FromQuery] string? predictionIds,
        CancellationToken cancellationToken)
    {
        var ids = ParseIds(predictionIds);
        if (ids.Count == 0)
        {
            return Ok(new Dictionary<int, ScoreNearMissHintSet>());
        }

        var hints = await _manualScoreLinkService.GetHintsAsync(ids, cancellationToken);
        return Ok(hints);
    }

    [HttpPost("confirm")]
    public async Task<ActionResult<ManualScoreConfirmResult>> Confirm(
        [FromBody] ManualScoreConfirmRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _manualScoreLinkService.ConfirmAsync(request, cancellationToken);
        if (!result.Success)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }

    private static List<int> ParseIds(string? predictionIds)
    {
        if (string.IsNullOrWhiteSpace(predictionIds))
        {
            return [];
        }

        return predictionIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList();
    }
}
