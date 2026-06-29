namespace MatchPredictor.Domain.Interfaces;

public interface IHistoricalDatasetService
{
    /// <summary>
    /// Serializes finished historical match scores within the given window to a deterministic,
    /// versioned JSON envelope suitable for committing as a reproducible backtest dataset.
    /// </summary>
    Task<string> ExportAsync(int lookbackDays, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a versioned dataset JSON into the store. When <paramref name="replaceExisting"/> is
    /// true the target window is cleared first; otherwise only previously-unseen fixtures are added.
    /// Returns the number of match rows imported.
    /// </summary>
    Task<int> ImportAsync(string datasetJson, bool replaceExisting, CancellationToken cancellationToken = default);
}
