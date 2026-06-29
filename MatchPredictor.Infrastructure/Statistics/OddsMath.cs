namespace MatchPredictor.Infrastructure.Statistics;

/// <summary>
/// Bookmaker odds / probability utilities. The most important job here is
/// <em>removing the bookmaker margin (vig/overround)</em> so that market-implied
/// probabilities can be used as honest model inputs instead of inflated quotes.
///
/// Three de-vig methods are provided:
/// <list type="bullet">
/// <item><b>Multiplicative</b> (a.k.a. proportional / basic normalization).</item>
/// <item><b>Power</b> — finds an exponent so the fair probabilities sum to 1; tends to
/// shade favourite-longshot bias better than multiplicative.</item>
/// <item><b>Shin</b> — models the margin as protection against insider traders; widely
/// regarded as the most accurate closed-form de-vig for 2-3 outcome markets.</item>
/// </list>
/// </summary>
public static class OddsMath
{
    private const double Epsilon = 1e-9;

    /// <summary>Implied (raw, still vigged) probability of a single decimal-odds quote.</summary>
    public static double ImpliedProbability(double decimalOdds)
    {
        return decimalOdds <= 1.0 ? 0.0 : 1.0 / decimalOdds;
    }

    /// <summary>Booksum (sum of implied probabilities). Overround = booksum - 1.</summary>
    public static double Booksum(IReadOnlyList<double> decimalOdds)
    {
        var sum = 0.0;
        for (var i = 0; i < decimalOdds.Count; i++)
        {
            sum += ImpliedProbability(decimalOdds[i]);
        }

        return sum;
    }

    /// <summary>Overround / margin baked into the quoted odds (e.g. 0.05 == 5%).</summary>
    public static double Overround(IReadOnlyList<double> decimalOdds)
    {
        return Booksum(decimalOdds) - 1.0;
    }

    /// <summary>
    /// Multiplicative (proportional) de-vig: scale each implied probability so the
    /// set sums to exactly 1.
    /// </summary>
    public static double[] FairProbabilitiesMultiplicative(IReadOnlyList<double> decimalOdds)
    {
        var implied = new double[decimalOdds.Count];
        var sum = 0.0;
        for (var i = 0; i < decimalOdds.Count; i++)
        {
            implied[i] = ImpliedProbability(decimalOdds[i]);
            sum += implied[i];
        }

        return NormalizeImplied(implied, sum);
    }

    /// <summary>
    /// Power de-vig: find an exponent k such that sum(implied_i ^ k) == 1, then take
    /// fair_i = implied_i ^ k. Because each implied probability is &lt; 1, increasing k
    /// shrinks the sum monotonically, so a bisection converges reliably.
    /// </summary>
    public static double[] FairProbabilitiesPower(IReadOnlyList<double> decimalOdds)
    {
        var implied = new double[decimalOdds.Count];
        var sum = 0.0;
        for (var i = 0; i < decimalOdds.Count; i++)
        {
            implied[i] = ImpliedProbability(decimalOdds[i]);
            sum += implied[i];
        }

        if (sum <= Epsilon)
        {
            return UniformDistribution(decimalOdds.Count);
        }

        // No margin (or negative) — nothing to solve, just normalize proportionally.
        if (sum <= 1.0 + Epsilon)
        {
            return NormalizeImplied(implied, sum);
        }

        double SumWithExponent(double k)
        {
            var total = 0.0;
            for (var i = 0; i < implied.Length; i++)
            {
                total += Math.Pow(implied[i], k);
            }

            return total;
        }

        var low = 1.0;
        var high = 8.0;
        // Expand the upper bound in the unlikely event the margin is enormous.
        while (SumWithExponent(high) > 1.0 && high < 64.0)
        {
            high *= 2.0;
        }

        for (var iteration = 0; iteration < 100; iteration++)
        {
            var mid = (low + high) / 2.0;
            var value = SumWithExponent(mid);
            if (Math.Abs(value - 1.0) < 1e-12)
            {
                low = high = mid;
                break;
            }

            if (value > 1.0)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        var exponent = (low + high) / 2.0;
        var fair = new double[implied.Length];
        var fairSum = 0.0;
        for (var i = 0; i < implied.Length; i++)
        {
            fair[i] = Math.Pow(implied[i], exponent);
            fairSum += fair[i];
        }

        return NormalizeImplied(fair, fairSum);
    }

    /// <summary>
    /// Shin (1992) de-vig. Solves for the insider-trading proportion z so the implied
    /// fair probabilities sum to 1, then derives each fair probability from z.
    /// </summary>
    public static double[] FairProbabilitiesShin(IReadOnlyList<double> decimalOdds)
    {
        var implied = new double[decimalOdds.Count];
        var booksum = 0.0;
        for (var i = 0; i < decimalOdds.Count; i++)
        {
            implied[i] = ImpliedProbability(decimalOdds[i]);
            booksum += implied[i];
        }

        if (booksum <= Epsilon)
        {
            return UniformDistribution(decimalOdds.Count);
        }

        if (booksum <= 1.0 + Epsilon)
        {
            return NormalizeImplied(implied, booksum);
        }

        double[] FairForZ(double z)
        {
            var result = new double[implied.Length];
            for (var i = 0; i < implied.Length; i++)
            {
                var ri = implied[i];
                var inside = (z * z) + (4.0 * (1.0 - z) * ri * ri / booksum);
                var value = (Math.Sqrt(Math.Max(inside, 0.0)) - z) / (2.0 * (1.0 - z));
                result[i] = value;
            }

            return result;
        }

        double SumForZ(double z) => FairForZ(z).Sum();

        var low = 0.0;
        var high = 0.5;
        // SumForZ(0) == booksum (>1); increasing z lowers the sum toward 1.
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var mid = (low + high) / 2.0;
            var value = SumForZ(mid);
            if (Math.Abs(value - 1.0) < 1e-12)
            {
                low = high = mid;
                break;
            }

            if (value > 1.0)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        var fair = FairForZ((low + high) / 2.0);
        return NormalizeImplied(fair, fair.Sum());
    }

    private static double[] NormalizeImplied(double[] values, double sum)
    {
        if (sum <= Epsilon)
        {
            return UniformDistribution(values.Length);
        }

        var normalized = new double[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            normalized[i] = values[i] / sum;
        }

        return normalized;
    }

    private static double[] UniformDistribution(int count)
    {
        if (count <= 0)
        {
            return [];
        }

        var uniform = new double[count];
        var value = 1.0 / count;
        for (var i = 0; i < count; i++)
        {
            uniform[i] = value;
        }

        return uniform;
    }
}
