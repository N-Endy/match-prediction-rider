using System.Collections.Generic;
using System.Linq;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Infrastructure.Services;

public class DataAnalyzerService : IDataAnalyzerService
{
    private readonly IProbabilityCalculator _probabilityCalculator;

    public DataAnalyzerService(IProbabilityCalculator probabilityCalculator)
    {
        _probabilityCalculator = probabilityCalculator;
    }

    public IEnumerable<MatchData> BothTeamsScore(IEnumerable<MatchData> matches) =>
        matches.Where(m => 
            _probabilityCalculator.CalculateBttsProbability(m) >= PredictionThresholds.BTTSScoreThreshold);

    public IEnumerable<MatchData> OverTwoGoals(IEnumerable<MatchData> matches) =>
        matches.Where(m => 
            _probabilityCalculator.CalculateOverTwoGoalsProbability(m) >= 0.6);
    
    public IEnumerable<MatchData> Draw(IEnumerable<MatchData> matches)=>
        matches.Where(m => 
            _probabilityCalculator.CalculateDrawProbability(m) >= 0.30);
    
    public IEnumerable<MatchData> StraightWin(IEnumerable<MatchData> matches) =>
        matches.Where(m => 
            _probabilityCalculator.IsStrongHomeWin(m) || 
            _probabilityCalculator.IsStrongAwayWin(m));
}