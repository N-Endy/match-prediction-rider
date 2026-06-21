namespace MatchPredictor.Web.Configuration;

public sealed record RuntimeModeOptions(
    bool RunBackgroundJobs,
    bool BrowserScrapingEnabled,
    bool UserTrackingEnabled,
    bool UseExternalCron)
{
    public static RuntimeModeOptions FromConfiguration(IConfiguration configuration)
    {
        var runBackgroundJobs = GetBoolean(configuration, "RUN_BACKGROUND_JOBS", true);
        var browserScrapingEnabled = GetBoolean(configuration, "ENABLE_BROWSER_SCRAPING", runBackgroundJobs);
        var userTrackingEnabled = GetBoolean(configuration, "ENABLE_USER_TRACKING", true);
        var useExternalCron = GetBoolean(configuration, "USE_EXTERNAL_CRON", false);

        return new RuntimeModeOptions(runBackgroundJobs, browserScrapingEnabled, userTrackingEnabled, useExternalCron);
    }

    private static bool GetBoolean(IConfiguration configuration, string key, bool defaultValue)
    {
        var rawValue = configuration[key];
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return defaultValue;
        }

        if (bool.TryParse(rawValue, out var parsed))
        {
            return parsed;
        }

        return rawValue.Trim().ToLowerInvariant() switch
        {
            "1" => true,
            "0" => false,
            "yes" => true,
            "no" => false,
            "on" => true,
            "off" => false,
            _ => defaultValue
        };
    }
}
