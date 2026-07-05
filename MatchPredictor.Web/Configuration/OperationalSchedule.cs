namespace MatchPredictor.Web.Configuration;

internal static class OperationalSchedule
{
    internal const int ScoreUpdateIntervalMinutes = 30;

    /// <summary>
    /// Cron for the recent score-update job.
    /// </summary>
    internal static string ScoreUpdateCron => EveryMinutes(ScoreUpdateIntervalMinutes);

    internal static TimeSpan ScoreUpdateStaleAfter =>
        TimeSpan.FromMinutes(ScoreUpdateIntervalMinutes + 15);

    private static string EveryMinutes(int intervalMinutes)
    {
        if (intervalMinutes is < 1 or > 1439)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalMinutes));
        }

        if (intervalMinutes <= 59)
        {
            return $"*/{intervalMinutes} * * * *";
        }

        var hours = intervalMinutes / 60;
        var minutes = intervalMinutes % 60;

        return minutes == 0
            ? $"0 */{hours} * * *"
            : $"{minutes} */{hours} * * *";
    }
}
