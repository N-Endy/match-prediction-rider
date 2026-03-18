namespace MatchPredictor.Web.Configuration;

public sealed record RuntimeModeOptions(bool RunBackgroundJobs, bool BrowserScrapingEnabled)
{
    public static RuntimeModeOptions FromConfiguration(IConfiguration configuration)
    {
        var runBackgroundJobs = GetBoolean(configuration, "RUN_BACKGROUND_JOBS", true);
        var browserScrapingEnabled = GetBoolean(configuration, "ENABLE_BROWSER_SCRAPING", runBackgroundJobs);

        return new RuntimeModeOptions(runBackgroundJobs, browserScrapingEnabled);
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
