using System.Collections.Generic;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IRegressionPredictorService
{
    /// <summary>
    /// Generate regression-based predictions for upcoming matches using historical MatchScores.
    /// Returns RegressionPrediction objects with categories:
    /// - "Over2.5Goals"
    /// - "BTTS"
    /// - "StraightWin"
    /// </summary>
    IEnumerable<RegressionPrediction> GeneratePredictions(IEnumerable<MatchData> upcomingMatches);
}
