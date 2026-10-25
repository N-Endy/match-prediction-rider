using MatchPredictor.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;

namespace MatchPredictor.Web.Configuration;

public sealed record RuntimeModeOptions(
    bool RunBackgroundJobs,
    bool BrowserScrapingEnabled,
    bool UserTrackingEnabled,
    bool UseExternalCron)
{
    public static RuntimeModeOptions FromConfiguration(IConfiguration configuration)
    {
        var runBackgroundJobs = configuration.GetBooleanFlag(
            "RUN_BACKGROUND_JOBS",
            defaultValue: true,
            "ENABLE_BACKGROUND_JOBS",
            "BACKGROUND_JOBS_ENABLED");

        var browserScrapingEnabled = configuration.GetBooleanFlag(
            "ENABLE_BROWSER_SCRAPING",
            defaultValue: runBackgroundJobs,
            "BROWSER_SCRAPING_ENABLED");

        var userTrackingEnabled = configuration.GetBooleanFlag(
            "ENABLE_USER_TRACKING",
            defaultValue: true,
            "USER_TRACKING_ENABLED");

        var useExternalCron = configuration.GetBooleanFlag(
            "USE_EXTERNAL_CRON",
            defaultValue: false,
            "EXTERNAL_CRON");

        return new RuntimeModeOptions(runBackgroundJobs, browserScrapingEnabled, userTrackingEnabled, useExternalCron);
    }
}
