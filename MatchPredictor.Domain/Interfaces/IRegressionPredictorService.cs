using System.Collections.Generic;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IRegressionPredictorService
{
    /// <summary>
    /// Generate regression-based predictions for upcoming matches using historical MatchScores.
    /// Returns a set of Prediction objects with categories like
    /// - "Regression.BTTS"
    /// - "Regression.Over2.5Goals"
    /// - "Regression.StraightWin"
    /// </summary>
    IEnumerable<Prediction> GeneratePredictions(IEnumerable<MatchData> upcomingMatches);
}
