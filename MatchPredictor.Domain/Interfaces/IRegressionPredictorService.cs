using System.Collections.Generic;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IRegressionPredictorService
{
    /// <summary>
    /// Legacy interface retained for compatibility.
    /// TennisPredictor v1 does not use regression-generated predictions for publication or value-bet analysis.
    IEnumerable<RegressionPrediction> GeneratePredictions(IEnumerable<MatchData> upcomingMatches);
}
