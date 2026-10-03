namespace MatchPredictor.Domain.Models;

public sealed record CopulaAccumulatorResult(
    double IndependentProbability,
    double CopulaProbability,
    double TailDependenceRatio,
    double CorrelationRho,
    int LegCount);

public static class GaussianCopulaAccumulatorMath
{
    public const double DefaultLeagueEquicorrelation = 0.08;

    /// <summary>
    /// Computes the joint probability of an accumulator under an equicorrelated Gaussian copula.
    /// P(E1, ..., EM) = ∫ [ ∏ Φ((Φ⁻¹(pi) + √ρ z) / √(1 - ρ)) ] φ(z) dz
    /// </summary>
    public static CopulaAccumulatorResult EvaluateAccumulator(
        IReadOnlyList<double> marginalProbabilities,
        double correlationRho = DefaultLeagueEquicorrelation)
    {
        ArgumentNullException.ThrowIfNull(marginalProbabilities);

        if (marginalProbabilities.Count == 0)
        {
            return new CopulaAccumulatorResult(0.0, 0.0, 1.0, correlationRho, 0);
        }

        var clampedProbs = marginalProbabilities.Select(p => Math.Clamp(p, 0.0001, 0.9999)).ToList();

        var indepProb = 1.0;
        foreach (var p in clampedProbs)
        {
            indepProb *= p;
        }

        if (clampedProbs.Count == 1 || correlationRho <= 1e-6)
        {
            return new CopulaAccumulatorResult(
                IndependentProbability: indepProb,
                CopulaProbability: indepProb,
                TailDependenceRatio: 1.0,
                CorrelationRho: 0.0,
                LegCount: clampedProbs.Count);
        }

        var rho = Math.Clamp(correlationRho, 0.0, 0.95);
        var sqrtRho = Math.Sqrt(rho);
        var sqrtOneMinusRho = Math.Sqrt(1.0 - rho);

        var probits = clampedProbs.Select(Probit).ToList();

        // 1D numerical integration across standard normal z in [-5, 5] using Simpson's 3/8 composite rule
        const int nSteps = 120;
        const double zMin = -5.0;
        const double zMax = 5.0;
        var h = (zMax - zMin) / nSteps;

        var integral = 0.0;
        for (var i = 0; i <= nSteps; i++)
        {
            var z = zMin + i * h;
            var phiZ = StandardNormalPdf(z);

            var conditionalProd = 1.0;
            foreach (var probitVal in probits)
            {
                var arg = (probitVal + sqrtRho * z) / sqrtOneMinusRho;
                conditionalProd *= Phi(arg);
            }

            var weight = (i == 0 || i == nSteps) ? 1.0 : (i % 2 == 1 ? 4.0 : 2.0);
            integral += weight * (conditionalProd * phiZ);
        }

        var copulaProb = Math.Clamp((h / 3.0) * integral, 0.0, 1.0);
        var ratio = indepProb > 1e-12 ? copulaProb / indepProb : 1.0;

        return new CopulaAccumulatorResult(
            IndependentProbability: indepProb,
            CopulaProbability: copulaProb,
            TailDependenceRatio: ratio,
            CorrelationRho: rho,
            LegCount: clampedProbs.Count);
    }

    /// <summary>
    /// Computes the expected value EV = P * Odds - 1.0 under both independence and copula correlation.
    /// </summary>
    public static (double IndependentEv, double CopulaEv) CalculateEv(
        IReadOnlyList<double> marginalProbabilities,
        double combinedDecimalOdds,
        double correlationRho = DefaultLeagueEquicorrelation)
    {
        var result = EvaluateAccumulator(marginalProbabilities, correlationRho);
        var indepEv = (result.IndependentProbability * combinedDecimalOdds) - 1.0;
        var copulaEv = (result.CopulaProbability * combinedDecimalOdds) - 1.0;
        return (indepEv, copulaEv);
    }

    public static double StandardNormalPdf(double z)
    {
        const double invSqrt2Pi = 0.3989422804014327; // 1 / sqrt(2 * pi)
        return invSqrt2Pi * Math.Exp(-0.5 * z * z);
    }

    public static double Phi(double z)
    {
        return ModelPromotionGate.NormalCdf(z);
    }

    /// <summary>
    /// Inverse standard normal cumulative distribution function (probit) using rational approximation.
    /// </summary>
    public static double Probit(double p)
    {
        p = Math.Clamp(p, 1e-9, 1.0 - 1e-9);

        // Coefficients in rational approximations (Acklam's algorithm)
        double[] a = [-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00];
        double[] b = [-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01];
        double[] c = [-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00];
        double[] d = [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00];

        const double pLow = 0.02425;
        const double pHigh = 1.0 - pLow;

        double q, r;

        if (p < pLow)
        {
            q = Math.Sqrt(-2.0 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                   ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1.0);
        }

        if (p <= pHigh)
        {
            q = p - 0.5;
            r = q * q;
            return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q /
                   (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1.0);
        }

        q = Math.Sqrt(-2.0 * Math.Log(1.0 - p));
        return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1.0);
    }
}
