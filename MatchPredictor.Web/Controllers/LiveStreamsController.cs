using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using MatchPredictor.Infrastructure.Services;

namespace MatchPredictor.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class LiveStreamsController : ControllerBase
{
    private readonly IMemoryCache _cache;
    private const string CacheKey = "ActiveLiveStreams";

    public LiveStreamsController(IMemoryCache cache)
    {
        _cache = cache;
    }

    [HttpGet]
    public IActionResult Get()
    {
        if (_cache.TryGetValue(CacheKey, out List<LiveStream>? streams))
        {
            return Ok(streams ?? new List<LiveStream>());
        }

        return Ok(new List<LiveStream>());
    }
}
