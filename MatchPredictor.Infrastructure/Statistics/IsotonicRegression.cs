namespace MatchPredictor.Infrastructure.Statistics;

/// <summary>
/// Pool-adjacent-violators isotonic regression for probability calibration.
/// </summary>
public static class IsotonicRegression
{
    public sealed record Knot(double Input, double Output);

    public static IReadOnlyList<Knot> Fit(IReadOnlyList<(double Input, bool Outcome, double Weight)> samples)
    {
        if (samples.Count == 0)
        {
            return [];
        }

        var ordered = samples
            .OrderBy(sample => sample.Input)
            .Select(sample => (Input: sample.Input, Rate: sample.Outcome ? 1.0 : 0.0, Weight: Math.Max(sample.Weight, 1e-6)))
            .ToList();

        var values = ordered.Select(item => item.Rate).ToArray();
        var weights = ordered.Select(item => item.Weight).ToArray();
        var inputs = ordered.Select(item => item.Input).ToArray();

        PoolAdjacentViolators(values, weights);

        var knots = new List<Knot>();
        var start = 0;
        for (var i = 1; i <= values.Length; i++)
        {
            if (i == values.Length || Math.Abs(values[i] - values[start]) > 1e-9)
            {
                knots.Add(new Knot(inputs[start], Math.Clamp(values[start], 0.0, 1.0)));
                start = i;
            }
        }

        return knots;
    }

    public static double Apply(double input, IReadOnlyList<Knot> knots)
    {
        if (knots.Count == 0)
        {
            return input;
        }

        if (input <= knots[0].Input)
        {
            return knots[0].Output;
        }

        if (input >= knots[^1].Input)
        {
            return knots[^1].Output;
        }

        for (var i = 1; i < knots.Count; i++)
        {
            if (input <= knots[i].Input)
            {
                var left = knots[i - 1];
                var right = knots[i];
                if (Math.Abs(right.Input - left.Input) < 1e-9)
                {
                    return right.Output;
                }

                var t = (input - left.Input) / (right.Input - left.Input);
                return Math.Clamp(left.Output + (t * (right.Output - left.Output)), 0.0, 1.0);
            }
        }

        return knots[^1].Output;
    }

    private static void PoolAdjacentViolators(double[] values, double[] weights)
    {
        var n = values.Length;
        var blockStart = new int[n];
        var blockEnd = new int[n];
        var blockWeight = new double[n];
        var blockValue = new double[n];
        var blockCount = 0;

        for (var i = 0; i < n; i++)
        {
            blockStart[blockCount] = i;
            blockEnd[blockCount] = i;
            blockWeight[blockCount] = weights[i];
            blockValue[blockCount] = values[i];
            blockCount++;

            while (blockCount > 1 && blockValue[blockCount - 2] > blockValue[blockCount - 1])
            {
                var mergedWeight = blockWeight[blockCount - 2] + blockWeight[blockCount - 1];
                var mergedValue = ((blockValue[blockCount - 2] * blockWeight[blockCount - 2]) +
                                   (blockValue[blockCount - 1] * blockWeight[blockCount - 1])) / mergedWeight;
                blockEnd[blockCount - 2] = blockEnd[blockCount - 1];
                blockWeight[blockCount - 2] = mergedWeight;
                blockValue[blockCount - 2] = mergedValue;
                blockCount--;
            }
        }

        var index = 0;
        for (var block = 0; block < blockCount; block++)
        {
            for (var i = blockStart[block]; i <= blockEnd[block]; i++)
            {
                values[index++] = blockValue[block];
            }
        }
    }
}
