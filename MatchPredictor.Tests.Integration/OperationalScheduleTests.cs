using MatchPredictor.Web.Configuration;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class OperationalScheduleTests
{
    [Fact]
    public void ScoreUpdateCron_Uses30MinuteInterval()
    {
        Assert.Equal(30, OperationalSchedule.ScoreUpdateIntervalMinutes);
        Assert.Equal("*/30 * * * *", OperationalSchedule.ScoreUpdateCron);
    }
}
