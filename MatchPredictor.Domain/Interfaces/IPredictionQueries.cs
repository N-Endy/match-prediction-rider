using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IPredictionQueries
{
    Task<IReadOnlyList<Prediction>> GetMatchWinnerAsync(DateTime date);
    Task<IReadOnlyList<Prediction>> GetOverUnderSetsAsync(DateTime date);
    Task<IReadOnlyList<Prediction>> GetSetHandicapAsync(DateTime date);
    Task<IReadOnlyList<Prediction>> GetCombinedSampleAsync(DateTime date, int count);
}
