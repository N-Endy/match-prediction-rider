using MatchPredictor.Infrastructure.Statistics;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class IsotonicRegressionTests
{
    [Fact]
    public void Fit_MonotoneIncreasingSamples_ProducesMonotoneKnots()
    {
        var samples = Enumerable.Range(1, 20)
            .Select(i => (Input: i / 20.0, Outcome: i >= 10, Weight: 1.0))
            .ToList();

        var knots = IsotonicRegression.Fit(samples);

        Assert.NotEmpty(knots);
        for (var i = 1; i < knots.Count; i++)
        {
            Assert.True(knots[i].Output >= knots[i - 1].Output);
            Assert.True(knots[i].Input >= knots[i - 1].Input);
        }
    }

    [Fact]
    public void Apply_InterpolatesBetweenKnots()
    {
        var knots = new[]
        {
            new IsotonicRegression.Knot(0.2, 0.1),
            new IsotonicRegression.Knot(0.8, 0.9)
        };

        var midpoint = IsotonicRegression.Apply(0.5, knots);

        Assert.InRange(midpoint, 0.45, 0.55);
    }

    [Fact]
    public void Apply_ClampsOutsideKnotRange()
    {
        var knots = new[]
        {
            new IsotonicRegression.Knot(0.3, 0.25),
            new IsotonicRegression.Knot(0.7, 0.75)
        };

        Assert.Equal(0.25, IsotonicRegression.Apply(0.0, knots));
        Assert.Equal(0.75, IsotonicRegression.Apply(1.0, knots));
    }
}
