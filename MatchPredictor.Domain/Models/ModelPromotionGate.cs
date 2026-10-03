namespace MatchPredictor.Domain.Models;

public sealed record ModelPromotionGateResult(
    bool ShouldPromote,
    double PValue,
    double ChampionLoss,
    double ChallengerLoss,
    double Improvement,
    int SampleCount,
    double TestStatistic,
    string Summary);

public static class ModelPromotionGate
{
    public const int DefaultMinimumSampleCount = 100;
    public const double DefaultAlpha = 0.05;

    public static ModelPromotionGateResult Evaluate(
        IReadOnlyList<double> championLosses,
        IReadOnlyList<double> challengerLosses,
        int minSamples = DefaultMinimumSampleCount,
        double alpha = DefaultAlpha)
    {
        ArgumentNullException.ThrowIfNull(championLosses);
        ArgumentNullException.ThrowIfNull(challengerLosses);

        if (championLosses.Count != challengerLosses.Count)
        {
            throw new ArgumentException("Champion and challenger loss collections must have equal sample counts.");
        }

        var n = championLosses.Count;
        if (n < minSamples)
        {
            return new ModelPromotionGateResult(
                ShouldPromote: false,
                PValue: 1.0,
                ChampionLoss: championLosses.Count > 0 ? championLosses.Average() : 0.0,
                ChallengerLoss: challengerLosses.Count > 0 ? challengerLosses.Average() : 0.0,
                Improvement: 0.0,
                SampleCount: n,
                TestStatistic: 0.0,
                Summary: $"Insufficient samples: {n} < {minSamples} required.");
        }

        var champMean = championLosses.Average();
        var challMean = challengerLosses.Average();
        var improvement = champMean - challMean;

        // If challenger is not better on average, immediately reject
        if (improvement <= 0)
        {
            return new ModelPromotionGateResult(
                ShouldPromote: false,
                PValue: 1.0,
                ChampionLoss: champMean,
                ChallengerLoss: challMean,
                Improvement: improvement,
                SampleCount: n,
                TestStatistic: 0.0,
                Summary: $"Challenger loss ({challMean:F4}) did not beat champion loss ({champMean:F4}).");
        }

        // Wilcoxon Signed-Rank Test on paired differences: d_i = champ_i - chall_i
        // Higher d_i means champion had higher loss -> challenger was better
        var diffs = new List<double>(n);
        for (var i = 0; i < n; i++)
        {
            diffs.Add(championLosses[i] - challengerLosses[i]);
        }

        var nonZero = diffs.Where(d => Math.Abs(d) > 1e-9).ToList();
        var nr = nonZero.Count;
        if (nr < 10)
        {
            return new ModelPromotionGateResult(
                ShouldPromote: false,
                PValue: 1.0,
                ChampionLoss: champMean,
                ChallengerLoss: challMean,
                Improvement: improvement,
                SampleCount: n,
                TestStatistic: 0.0,
                Summary: "Too few non-zero differences to establish statistical significance.");
        }

        // Order by absolute difference
        var indexed = nonZero
            .Select((d, idx) => (Diff: d, AbsDiff: Math.Abs(d)))
            .OrderBy(x => x.AbsDiff)
            .ToList();

        // Assign fractional ranks for ties
        var ranks = new double[nr];
        var iRank = 0;
        var tieAdjustment = 0.0;

        while (iRank < nr)
        {
            var jRank = iRank;
            while (jRank < nr - 1 && Math.Abs(indexed[jRank + 1].AbsDiff - indexed[iRank].AbsDiff) < 1e-9)
            {
                jRank++;
            }

            var groupSize = jRank - iRank + 1;
            var avgRank = (iRank + 1 + jRank + 1) / 2.0;

            for (var k = iRank; k <= jRank; k++)
            {
                ranks[k] = avgRank;
            }

            if (groupSize > 1)
            {
                tieAdjustment += (Math.Pow(groupSize, 3) - groupSize);
            }

            iRank = jRank + 1;
        }

        var wPlus = 0.0;
        for (var i = 0; i < nr; i++)
        {
            if (indexed[i].Diff > 0)
            {
                wPlus += ranks[i];
            }
        }

        var meanW = nr * (nr + 1.0) / 4.0;
        var varW = (nr * (nr + 1.0) * (2.0 * nr + 1.0) - (tieAdjustment / 2.0)) / 24.0;
        var stdW = Math.Sqrt(Math.Max(1e-9, varW));

        // Continuity correction
        var z = (wPlus - meanW - 0.5 * Math.Sign(wPlus - meanW)) / stdW;

        // One-tailed p-value for H1: challenger loss < champion loss (wPlus > meanW)
        var pValue = 1.0 - NormalCdf(z);

        var shouldPromote = pValue < alpha && improvement > 0;
        var summary = shouldPromote
            ? $"Challenger cleared promotion gate: improvement = {improvement:F4}, p-value = {pValue:F4} (< {alpha:F2}), N = {n}."
            : $"Promotion rejected: p-value = {pValue:F4} (threshold = {alpha:F2}), N = {n}.";

        return new ModelPromotionGateResult(
            ShouldPromote: shouldPromote,
            PValue: pValue,
            ChampionLoss: champMean,
            ChallengerLoss: challMean,
            Improvement: improvement,
            SampleCount: n,
            TestStatistic: z,
            Summary: summary);
    }

    /// <summary>
    /// Cumulative distribution function for standard normal distribution N(0, 1).
    /// </summary>
    public static double NormalCdf(double z)
    {
        return 0.5 * (1.0 + Erf(z / Math.Sqrt(2.0)));
    }

    /// <summary>
    /// Error function approximation (Chebyshev fitting, maximal error 1.5e-7).
    /// </summary>
    public static double Erf(double x)
    {
        var sign = x < 0 ? -1.0 : 1.0;
        x = Math.Abs(x);

        var t = 1.0 / (1.0 + 0.3275911 * x);
        var poly = t * (0.254829592 + t * (-0.284496736 + t * (1.421413741 + t * (-1.453152027 + t * 1.061405429))));

        return sign * (1.0 - poly * Math.Exp(-x * x));
    }
}
