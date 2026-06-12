using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace MatchPredictor.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ValueBetsController : ControllerBase
{
    private const string ReportCacheKey = "valuebets:report:60";
    private static readonly TimeSpan ReportCacheTtl = TimeSpan.FromMinutes(3);

    private readonly IValueBetsService _valueBetsService;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ValueBetsController> _logger;

    public ValueBetsController(
        IValueBetsService valueBetsService,
        IMemoryCache cache,
        ILogger<ValueBetsController> logger)
    {
        _valueBetsService = valueBetsService;
        _cache = cache;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetValueBets(CancellationToken ct)
    {
        try
        {
            // The report involves a full recompute plus an AI call; cache briefly so
            // bursts of page loads do not multiply that cost.
            if (_cache.TryGetValue(ReportCacheKey, out ValueBetReportDto? cachedReport) && cachedReport is not null)
            {
                return Ok(cachedReport);
            }

            var report = await _valueBetsService.GetValueBetReportAsync(60, ct);
            _cache.Set(ReportCacheKey, report, ReportCacheTtl);
            return Ok(report);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch Value Bets");
            return Ok(new ValueBetReportDto
            {
                GeneratedAtLocal = DateTimeProvider.GetLocalTime(),
                Warnings =
                [
                    "Value Bets could not be fully loaded right now. Please refresh in a moment."
                ]
            });
        }
    }
}
