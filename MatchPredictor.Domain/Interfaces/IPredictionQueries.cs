using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IPredictionQueries
{
    Task<IReadOnlyList<Prediction>> GetBTTSAsync(DateTime date);
    Task<IReadOnlyList<Prediction>> GetOver25Async(DateTime date);
    Task<IReadOnlyList<Prediction>> GetUnder25Async(DateTime date);
    Task<IReadOnlyList<Prediction>> GetStraightWinAsync(DateTime date);
    Task<IReadOnlyList<Prediction>> GetDrawAsync(DateTime date);
    Task<IReadOnlyList<Prediction>> GetOver15Async(DateTime date) => Task.FromResult<IReadOnlyList<Prediction>>([]);
    Task<IReadOnlyList<Prediction>> GetDoubleChanceAsync(DateTime date) => Task.FromResult<IReadOnlyList<Prediction>>([]);

    /// <summary>
    /// Recent settled, published, current-revision picks for the public results track record.
    /// </summary>
    Task<IReadOnlyList<Prediction>> GetRecentSettledPublishedAsync(int days = 30);
}
