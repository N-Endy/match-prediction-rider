using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IPredictionQueries
{
    Task<IReadOnlyList<Prediction>> GetBTTSAsync(DateTime date);
    Task<IReadOnlyList<Prediction>> GetOver25Async(DateTime date);
    Task<IReadOnlyList<Prediction>> GetDrawAsync(DateTime date);
    Task<IReadOnlyList<Prediction>> GetStraightWinAsync(DateTime date);
    Task<IReadOnlyList<Prediction>> GetCombinedSampleAsync(DateTime date, int count);
}

