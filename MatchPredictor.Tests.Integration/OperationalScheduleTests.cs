using MatchPredictor.Web.Configuration;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class OperationalScheduleTests
{
    [Fact]
    public void ScoreUpdateCron_Uses12MinuteInterval()
    {
        Assert.Equal(12, OperationalSchedule.ScoreUpdateIntervalMinutes);
        Assert.Equal("*/12 * * * *", OperationalSchedule.ScoreUpdateCron);
    }
}
