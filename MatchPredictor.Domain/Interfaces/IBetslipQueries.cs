using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IBetslipQueries
{
    Task<BetslipSet?> GetCurrentSetAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DateOnly>> GetSlipDatesAsync(
        BetslipRecordSection section,
        int year,
        int month,
        CancellationToken ct = default);

    Task<DateOnly?> GetLatestSlipDateAsync(
        BetslipRecordSection section,
        CancellationToken ct = default);

    Task<BetslipRecordsForDate> GetSlipsForDateAsync(
        BetslipRecordSection section,
        DateOnly date,
        CancellationToken ct = default);
}
