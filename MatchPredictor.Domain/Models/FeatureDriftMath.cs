namespace MatchPredictor.Domain.Models;

public enum DriftSeverity
{
    None = 0,
    Moderate = 1,
    Significant = 2
}

public sealed record FeatureDriftResult(
    string FeatureName,
    double PopulationStabilityIndex,
    double WassersteinDistance,
    DriftSeverity Severity,
    int ReferenceSampleCount,
    int TargetSampleCount);

public static class FeatureDriftMath
{
    public const double ModeratePsiThreshold = 0.10;
    public const double SignificantPsiThreshold = 0.25;

    public static FeatureDriftResult EvaluateFeature(
        string featureName,
        IReadOnlyList<double> referenceValues,
        IReadOnlyList<double> targetValues,
        int binCount = 10)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);
        ArgumentNullException.ThrowIfNull(referenceValues);
        ArgumentNullException.ThrowIfNull(targetValues);

        if (referenceValues.Count == 0 || targetValues.Count == 0)
        {
            return new FeatureDriftResult(
                featureName,
                PopulationStabilityIndex: 0.0,
                WassersteinDistance: 0.0,
                Severity: DriftSeverity.None,
                ReferenceSampleCount: referenceValues.Count,
                TargetSampleCount: targetValues.Count);
        }

        var psi = CalculatePsi(referenceValues, targetValues, binCount);
        var wasserstein = CalculateWassersteinDistance(referenceValues, targetValues);

        var severity = psi >= SignificantPsiThreshold
            ? DriftSeverity.Significant
            : psi >= ModeratePsiThreshold
                ? DriftSeverity.Moderate
                : DriftSeverity.None;

        return new FeatureDriftResult(
            featureName,
            PopulationStabilityIndex: psi,
            WassersteinDistance: wasserstein,
            Severity: severity,
            ReferenceSampleCount: referenceValues.Count,
            TargetSampleCount: targetValues.Count);
    }

    public static double CalculatePsi(
        IReadOnlyList<double> referenceValues,
        IReadOnlyList<double> targetValues,
        int binCount = 10,
        double epsilon = 1e-4)
    {
        ArgumentNullException.ThrowIfNull(referenceValues);
        ArgumentNullException.ThrowIfNull(targetValues);

        if (referenceValues.Count == 0 || targetValues.Count == 0)
        {
            return 0.0;
        }

        binCount = Math.Clamp(binCount, 2, 50);

        var sortedRef = referenceValues.OrderBy(x => x).ToList();
        var binCutoffs = new double[binCount - 1];

        for (var i = 0; i < binCount - 1; i++)
        {
            var quantile = (double)(i + 1) / binCount;
            var index = (int)Math.Floor(quantile * (sortedRef.Count - 1));
            binCutoffs[i] = sortedRef[index];
        }

        // Count bins for reference
        var refCounts = new int[binCount];
        foreach (var val in referenceValues)
        {
            var bin = GetBinIndex(val, binCutoffs);
            refCounts[bin]++;
        }

        // Count bins for target
        var targetCounts = new int[binCount];
        foreach (var val in targetValues)
        {
            var bin = GetBinIndex(val, binCutoffs);
            targetCounts[bin]++;
        }

        var totalRef = (double)referenceValues.Count;
        var totalTarget = (double)targetValues.Count;

        var psi = 0.0;
        for (var i = 0; i < binCount; i++)
        {
            var refProp = ((refCounts[i] / totalRef) + epsilon);
            var targetProp = ((targetCounts[i] / totalTarget) + epsilon);

            psi += (targetProp - refProp) * Math.Log(targetProp / refProp);
        }

        return Math.Max(0.0, psi);
    }

    public static double CalculateWassersteinDistance(
        IReadOnlyList<double> referenceValues,
        IReadOnlyList<double> targetValues)
    {
        ArgumentNullException.ThrowIfNull(referenceValues);
        ArgumentNullException.ThrowIfNull(targetValues);

        if (referenceValues.Count == 0 || targetValues.Count == 0)
        {
            return 0.0;
        }

        var sortedRef = referenceValues.OrderBy(x => x).ToList();
        var sortedTarget = targetValues.OrderBy(x => x).ToList();

        if (sortedRef.Count == sortedTarget.Count)
        {
            var sum = 0.0;
            for (var i = 0; i < sortedRef.Count; i++)
            {
                sum += Math.Abs(sortedRef[i] - sortedTarget[i]);
            }
            return sum / sortedRef.Count;
        }

        // Stepwise quantile integration across common points
        const int steps = 200;
        var totalDist = 0.0;

        for (var i = 0; i < steps; i++)
        {
            var t = (i + 0.5) / steps;
            var qRef = Quantile(sortedRef, t);
            var qTarget = Quantile(sortedTarget, t);
            totalDist += Math.Abs(qRef - qTarget);
        }

        return totalDist / steps;
    }

    private static int GetBinIndex(double value, double[] cutoffs)
    {
        for (var i = 0; i < cutoffs.Length; i++)
        {
            if (value <= cutoffs[i])
            {
                return i;
            }
        }
        return cutoffs.Length;
    }

    private static double Quantile(List<double> sorted, double t)
    {
        var pos = t * (sorted.Count - 1);
        var low = (int)Math.Floor(pos);
        var high = (int)Math.Ceiling(pos);
        var weight = pos - low;

        if (low == high)
        {
            return sorted[low];
        }

        return (1.0 - weight) * sorted[low] + weight * sorted[high];
    }
}
