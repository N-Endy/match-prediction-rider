using MatchPredictor.Infrastructure.Utils;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class RecencyWeightingTests
{
    [Fact]
    public void CalculateWeight_ReturnsOne_ForCurrentTimestamp()
    {
        var weight = RecencyWeighting.CalculateWeight(DateTime.UtcNow, halfLifeDays: 30.0);

        Assert.Equal(1.0, weight, 6);
    }

    [Fact]
    public void CalculateWeight_Halves_AfterOneHalfLife()
    {
        var timestamp = DateTime.UtcNow.AddDays(-30.0);

        var weight = RecencyWeighting.CalculateWeight(timestamp, halfLifeDays: 30.0);

        Assert.Equal(0.5, weight, 6);
    }

    [Fact]
    public void CalculateWeight_NeverReturnsNegative_ForFutureTimestamp()
    {
        var weight = RecencyWeighting.CalculateWeight(DateTime.UtcNow.AddHours(2), halfLifeDays: 21.0);

        Assert.Equal(1.0, weight, 6);
    }
}
