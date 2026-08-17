using MatchPredictor.Domain.Helpers;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class UnsupportedFixtureFilterTests
{
    [Theory]
    [InlineData("Charlotte FC (Bookings)", "Columbus Crew (Bookings)")]
    [InlineData("Charlotte FC (Bookings)", "Columbus Crew")]
    [InlineData("Charlotte FC", "Columbus Crew (Bookings)")]
    [InlineData("Watford (bookings)", "Southampton")]
    public void IsBookingsFixture_ReturnsTrue_WhenEitherTeamHasBookingsMarker(string home, string away)
    {
        Assert.True(UnsupportedFixtureFilter.IsBookingsFixture(home, away));
    }

    [Theory]
    [InlineData("Charlotte FC", "Columbus Crew")]
    [InlineData("Watford", "Southampton")]
    [InlineData(null, "Columbus Crew")]
    [InlineData("Charlotte FC", "")]
    public void IsBookingsFixture_ReturnsFalse_ForNormalFixtures(string? home, string? away)
    {
        Assert.False(UnsupportedFixtureFilter.IsBookingsFixture(home, away));
    }
}
