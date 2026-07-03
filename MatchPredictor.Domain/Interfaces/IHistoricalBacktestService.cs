namespace MatchPredictor.Domain.Interfaces;

public interface IHistoricalBacktestService
{
    Task RunNightlyBacktestAsync(CancellationToken cancellationToken = default);
}
