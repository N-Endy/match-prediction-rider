using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface ISameGameMultiService
{
    SameGameMultiResult Evaluate(
        double[,] scoreProbabilityMatrix,
        IReadOnlyList<SameGameMultiLeg> legs);

    SameGameMultiResult Evaluate(
        double homeLambda,
        double awayMu,
        double rho,
        IReadOnlyList<SameGameMultiLeg> legs);

    IReadOnlyList<SameGameMultiResult> FindCuratedCombinations(
        double homeLambda,
        double awayMu,
        double rho,
        double minFairOdds = 2.0,
        double maxFairOdds = 8.0);
}
