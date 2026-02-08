using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IMatchDataRepository
{
    Task<List<MatchData>> GetMatchDataAsync(DateTime? date = null);
}