using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using Microsoft.Extensions.Caching.Memory;

namespace MatchPredictor.Web.Services;

public class CachedBetslipQueries : IBetslipQueries
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);
    private const string CurrentCacheKey = "betslips:current";

    private readonly IBetslipQueries _inner;
    private readonly IMemoryCache _cache;

    public CachedBetslipQueries(IBetslipQueries inner, IMemoryCache cache)
    {
        _inner = inner;
        _cache = cache;
    }

    public async Task<BetslipSet?> GetCurrentSetAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CurrentCacheKey, out BetslipSet? cached))
        {
            return cached;
        }

        var result = await _inner.GetCurrentSetAsync(ct);
        _cache.Set(CurrentCacheKey, result, CacheTtl);
        return result;
    }

    public async Task<IReadOnlyList<DateOnly>> GetSlipDatesAsync(
        BetslipRecordSection section,
        int year,
        int month,
        CancellationToken ct = default)
    {
        var result = await _cache.GetOrCreateAsync(
            $"betslips:dates:{section}:{year:D4}-{month:D2}",
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheTtl;
                return await _inner.GetSlipDatesAsync(section, year, month, ct);
            });

        return result ?? [];
    }

    public Task<DateOnly?> GetLatestSlipDateAsync(
        BetslipRecordSection section,
        CancellationToken ct = default)
    {
        return _cache.GetOrCreateAsync(
            $"betslips:latest:{section}",
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheTtl;
                return await _inner.GetLatestSlipDateAsync(section, ct);
            });
    }

    public async Task<BetslipRecordsForDate> GetSlipsForDateAsync(
        BetslipRecordSection section,
        DateOnly date,
        CancellationToken ct = default)
    {
        var result = await _cache.GetOrCreateAsync(
            $"betslips:history:{section}:{date:yyyy-MM-dd}",
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheTtl;
                return await _inner.GetSlipsForDateAsync(section, date, ct);
            });

        return result ?? new BetslipRecordsForDate { Date = date, Section = section };
    }
}
