using MatchPredictor.Infrastructure.Services;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class SportyBetOverUnderSpecifierTests
{
    [Theory]
    [InlineData("total=2.5")]
    [InlineData("total=2.50")]
    [InlineData(" total=2.5 ")]
    [InlineData("TOTAL=2.50")]
    public void IsOverUnder25Specifier_AcceptsTwoPointFiveLines(string specifier)
    {
        Assert.True(SportyBetBookingService.IsOverUnder25Specifier(specifier));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("total=3.5")]
    [InlineData("2.5")]
    public void IsOverUnder25Specifier_RejectsOtherLines(string? specifier)
    {
        Assert.False(SportyBetBookingService.IsOverUnder25Specifier(specifier));
    }
}
