using MatchPredictor.Web.Configuration;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class OperationalScheduleTests
{
    [Fact]
    public void ScoreUpdateCron_Uses15MinuteInterval()
    {
        Assert.Equal(15, OperationalSchedule.ScoreUpdateIntervalMinutes);
        Assert.Equal("*/15 * * * *", OperationalSchedule.ScoreUpdateCron);
    }
}
