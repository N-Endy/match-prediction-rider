using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using Microsoft.Extensions.Caching.Memory;

namespace MatchPredictor.Web.Services;

/// <summary>
/// Short-TTL read cache over <see cref="IPredictionQueries"/> so the public
/// prediction pages do not hit Postgres on every request. Two minutes is well
/// inside the 6-minute score-update cadence.
/// </summary>
public class CachedPredictionQueries : IPredictionQueries
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);

    private readonly IPredictionQueries _inner;
    private readonly IMemoryCache _cache;

    public CachedPredictionQueries(IPredictionQueries inner, IMemoryCache cache)
    {
        _inner = inner;
        _cache = cache;
    }

    public Task<IReadOnlyList<Prediction>> GetBTTSAsync(DateTime date) =>
        GetOrCreateAsync($"predictions:btts:{date:yyyy-MM-dd}", () => _inner.GetBTTSAsync(date));

    public Task<IReadOnlyList<Prediction>> GetOver25Async(DateTime date) =>
        GetOrCreateAsync($"predictions:over25:{date:yyyy-MM-dd}", () => _inner.GetOver25Async(date));

    public Task<IReadOnlyList<Prediction>> GetUnder25Async(DateTime date) =>
        GetOrCreateAsync($"predictions:under25:{date:yyyy-MM-dd}", () => _inner.GetUnder25Async(date));

    public Task<IReadOnlyList<Prediction>> GetStraightWinAsync(DateTime date) =>
        GetOrCreateAsync($"predictions:straightwin:{date:yyyy-MM-dd}", () => _inner.GetStraightWinAsync(date));

    public Task<IReadOnlyList<Prediction>> GetDrawAsync(DateTime date) =>
        GetOrCreateAsync($"predictions:draw:{date:yyyy-MM-dd}", () => _inner.GetDrawAsync(date));

    public Task<IReadOnlyList<Prediction>> GetCombinedSampleAsync(DateTime date, int count) =>
        GetOrCreateAsync($"predictions:combined:{date:yyyy-MM-dd}:{count}", () => _inner.GetCombinedSampleAsync(date, count));

    private async Task<IReadOnlyList<Prediction>> GetOrCreateAsync(
        string cacheKey,
        Func<Task<IReadOnlyList<Prediction>>> factory)
    {
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<Prediction>? cached) && cached is not null)
        {
            return cached;
        }

        var result = await factory();
        _cache.Set(cacheKey, result, CacheTtl);
        return result;
    }
}
