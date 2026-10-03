using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Infrastructure.Services;

public sealed class SameGameMultiService : ISameGameMultiService
{
    private const int DefaultMaxGoals = 10;

    public SameGameMultiResult Evaluate(
        double[,] scoreProbabilityMatrix,
        IReadOnlyList<SameGameMultiLeg> legs)
    {
        ArgumentNullException.ThrowIfNull(scoreProbabilityMatrix);
        ArgumentNullException.ThrowIfNull(legs);

        if (legs.Count == 0)
        {
            return new SameGameMultiResult(
                IsValid: false,
                ExactJointProbability: 0.0,
                FairDecimalOdds: 0.0,
                IndependentProbability: 0.0,
                CorrelationFactor: 1.0,
                Legs: legs,
                RejectionReason: "No legs provided.");
        }

        var maxHome = scoreProbabilityMatrix.GetLength(0) - 1;
        var maxAway = scoreProbabilityMatrix.GetLength(1) - 1;

        var predicates = legs.Select(l => SameGameMultiPredicates.GetPredicate(l.Market)).ToList();

        // 1. Calculate marginal probability for each leg
        var marginals = new double[legs.Count];
        for (var i = 0; i < legs.Count; i++)
        {
            var pred = predicates[i];
            var p = 0.0;
            for (var h = 0; h <= maxHome; h++)
            {
                for (var a = 0; a <= maxAway; a++)
                {
                    if (pred(h, a))
                    {
                        p += scoreProbabilityMatrix[h, a];
                    }
                }
            }
            marginals[i] = p;
        }

        var indepProb = 1.0;
        foreach (var p in marginals)
        {
            indepProb *= p;
        }

        // 2. Calculate exact joint probability across the intersection of all predicates
        var jointProb = 0.0;
        for (var h = 0; h <= maxHome; h++)
        {
            for (var a = 0; a <= maxAway; a++)
            {
                var matchesAll = true;
                for (var i = 0; i < predicates.Count; i++)
                {
                    if (!predicates[i](h, a))
                    {
                        matchesAll = false;
                        break;
                    }
                }

                if (matchesAll)
                {
                    jointProb += scoreProbabilityMatrix[h, a];
                }
            }
        }

        if (jointProb < 1e-6)
        {
            return new SameGameMultiResult(
                IsValid: false,
                ExactJointProbability: 0.0,
                FairDecimalOdds: 0.0,
                IndependentProbability: indepProb,
                CorrelationFactor: 0.0,
                Legs: legs,
                RejectionReason: "Mutually exclusive or near-zero joint probability combination.");
        }

        var fairOdds = 1.0 / jointProb;
        var correlationFactor = indepProb > 1e-9 ? jointProb / indepProb : 1.0;

        return new SameGameMultiResult(
            IsValid: true,
            ExactJointProbability: jointProb,
            FairDecimalOdds: fairOdds,
            IndependentProbability: indepProb,
            CorrelationFactor: correlationFactor,
            Legs: legs);
    }

    public SameGameMultiResult Evaluate(
        double homeLambda,
        double awayMu,
        double rho,
        IReadOnlyList<SameGameMultiLeg> legs)
    {
        var matrix = BuildBivariatePoissonScoreMatrix(homeLambda, awayMu, rho, DefaultMaxGoals);
        return Evaluate(matrix, legs);
    }

    public IReadOnlyList<SameGameMultiResult> FindCuratedCombinations(
        double homeLambda,
        double awayMu,
        double rho,
        double minFairOdds = 2.0,
        double maxFairOdds = 8.0)
    {
        var matrix = BuildBivariatePoissonScoreMatrix(homeLambda, awayMu, rho, DefaultMaxGoals);

        SameGameMultiLeg[][] candidateSets =
        [
            [
                new(SameGameMultiMarket.HomeWin, "Home Win"),
                new(SameGameMultiMarket.Over15Goals, "Over 1.5 Goals")
            ],
            [
                new(SameGameMultiMarket.HomeWin, "Home Win"),
                new(SameGameMultiMarket.Over25Goals, "Over 2.5 Goals")
            ],
            [
                new(SameGameMultiMarket.HomeWin, "Home Win"),
                new(SameGameMultiMarket.BothTeamsScoreYes, "Both Teams To Score")
            ],
            [
                new(SameGameMultiMarket.HomeWin, "Home Win"),
                new(SameGameMultiMarket.HomeOver15Goals, "Home Score 2+ Goals")
            ],
            [
                new(SameGameMultiMarket.DoubleChance1X, "Home / Draw"),
                new(SameGameMultiMarket.Under35Goals, "Under 3.5 Goals")
            ],
            [
                new(SameGameMultiMarket.DoubleChance1X, "Home / Draw"),
                new(SameGameMultiMarket.Over15Goals, "Over 1.5 Goals")
            ],
            [
                new(SameGameMultiMarket.BothTeamsScoreYes, "Both Teams To Score"),
                new(SameGameMultiMarket.Over25Goals, "Over 2.5 Goals")
            ],
            [
                new(SameGameMultiMarket.Draw, "Draw"),
                new(SameGameMultiMarket.Under25Goals, "Under 2.5 Goals")
            ],
            [
                new(SameGameMultiMarket.AwayWin, "Away Win"),
                new(SameGameMultiMarket.Over15Goals, "Over 1.5 Goals")
            ],
            [
                new(SameGameMultiMarket.AwayWin, "Away Win"),
                new(SameGameMultiMarket.BothTeamsScoreYes, "Both Teams To Score")
            ],
            // 3-leg combinations
            [
                new(SameGameMultiMarket.HomeWin, "Home Win"),
                new(SameGameMultiMarket.BothTeamsScoreYes, "Both Teams To Score"),
                new(SameGameMultiMarket.Over25Goals, "Over 2.5 Goals")
            ],
            [
                new(SameGameMultiMarket.DoubleChance1X, "Home / Draw"),
                new(SameGameMultiMarket.BothTeamsScoreYes, "Both Teams To Score"),
                new(SameGameMultiMarket.Over25Goals, "Over 2.5 Goals")
            ]
        ];

        var results = new List<SameGameMultiResult>();
        foreach (var combo in candidateSets)
        {
            var res = Evaluate(matrix, combo);
            if (res.IsValid && res.FairDecimalOdds >= minFairOdds && res.FairDecimalOdds <= maxFairOdds)
            {
                results.Add(res);
            }
        }

        return results.OrderBy(r => r.FairDecimalOdds).ToList();
    }

    private static double[,] BuildBivariatePoissonScoreMatrix(
        double lambda,
        double mu,
        double rho,
        int maxGoals)
    {
        lambda = Math.Clamp(lambda, 0.05, 10.0);
        mu = Math.Clamp(mu, 0.05, 10.0);

        var homePmf = PoissonPmfVector(lambda, maxGoals);
        var awayPmf = PoissonPmfVector(mu, maxGoals);

        var matrix = new double[maxGoals + 1, maxGoals + 1];
        var total = 0.0;

        for (var h = 0; h <= maxGoals; h++)
        {
            for (var a = 0; a <= maxGoals; a++)
            {
                var prob = homePmf[h] * awayPmf[a] * DixonColesTau(h, a, lambda, mu, rho);
                prob = Math.Max(0.0, prob);
                matrix[h, a] = prob;
                total += prob;
            }
        }

        if (total > 0.0)
        {
            for (var h = 0; h <= maxGoals; h++)
            {
                for (var a = 0; a <= maxGoals; a++)
                {
                    matrix[h, a] /= total;
                }
            }
        }

        return matrix;
    }

    private static double[] PoissonPmfVector(double mean, int max)
    {
        var pmf = new double[max + 1];
        pmf[0] = Math.Exp(-mean);

        for (var k = 1; k <= max; k++)
        {
            pmf[k] = pmf[k - 1] * mean / k;
        }

        return pmf;
    }

    private static double DixonColesTau(int h, int a, double lambda, double mu, double rho)
    {
        if (h == 0 && a == 0) return 1.0 - (lambda * mu * rho);
        if (h == 0 && a == 1) return 1.0 + (lambda * rho);
        if (h == 1 && a == 0) return 1.0 + (mu * rho);
        if (h == 1 && a == 1) return 1.0 - rho;
        return 1.0;
    }
}
