namespace MatchPredictor.Domain.Interfaces;

public interface ITeamMatchStatsSyncService
{
    /// <summary>
    /// Upserts aggregated per-team rows from finished MatchScores (goals only).
    /// ExpectedGoals remain null until a licensed xG source is connected.
    /// </summary>
    Task<int> SyncFromMatchScoresAsync(int lookbackDays = 600, CancellationToken cancellationToken = default);
}
