using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using Microsoft.Extensions.Caching.Memory;

namespace MatchPredictor.Web.Services;

public class CachedBetslipQueries : IBetslipQueries
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);
    private const string CacheKey = "betslips:current";

    private readonly IBetslipQueries _inner;
    private readonly IMemoryCache _cache;

    public CachedBetslipQueries(IBetslipQueries inner, IMemoryCache cache)
    {
        _inner = inner;
        _cache = cache;
    }

    public async Task<BetslipSet?> GetCurrentSetAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheKey, out BetslipSet? cached))
        {
            return cached;
        }

        var result = await _inner.GetCurrentSetAsync(ct);
        _cache.Set(CacheKey, result, CacheTtl);
        return result;
    }
}
