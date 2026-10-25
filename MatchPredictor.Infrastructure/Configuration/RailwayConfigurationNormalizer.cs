using System.Collections;
using Microsoft.Extensions.Configuration;

namespace MatchPredictor.Infrastructure.Configuration;

/// <summary>
/// Normalizes Railway/PaaS environment variables into standard .NET configuration keys
/// so that both legacy .NET configuration lookups and simplified Railway environment variables work seamlessly.
/// </summary>
public static class RailwayConfigurationNormalizer
{
    /// <summary>
    /// Builds a normalized dictionary of configuration keys from the current environment and/or existing configuration.
    /// Only keys that have actual environment variables or aliases present are added.
    /// </summary>
    public static IDictionary<string, string?> BuildNormalizedMap(
        IConfiguration? existingConfig = null,
        IDictionary? envVars = null)
    {
        envVars ??= Environment.GetEnvironmentVariables();
        var normalized = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        string? Lookup(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                // Check environment variables first
                if (envVars.Contains(key))
                {
                    var val = envVars[key]?.ToString();
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        return val.Trim();
                    }
                }

                // Check existing configuration if provided
                if (existingConfig != null)
                {
                    var val = existingConfig[key];
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        return val.Trim();
                    }
                }
            }

            return null;
        }

        void MapBoolean(string targetKey, params string[] aliases)
        {
            var raw = Lookup(aliases);
            if (raw != null && ConfigurationFlagExtensions.TryParseBoolean(raw, out var parsed))
            {
                var boolStr = parsed ? "true" : "false";
                normalized[targetKey] = boolStr;

                // Also populate uppercase Railway aliases so any lookup pattern works
                foreach (var alias in aliases)
                {
                    normalized[alias] = boolStr;
                }
            }
        }

        void MapString(string targetKey, params string[] aliases)
        {
            var raw = Lookup(aliases);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                normalized[targetKey] = raw;
                foreach (var alias in aliases)
                {
                    normalized[alias] = raw;
                }
            }
        }

        void MapInt(string targetKey, params string[] aliases)
        {
            var raw = Lookup(aliases);
            if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out var parsedInt) && parsedInt > 0)
            {
                var intStr = parsedInt.ToString();
                normalized[targetKey] = intStr;
                foreach (var alias in aliases)
                {
                    normalized[alias] = intStr;
                }
            }
        }

        // 1. SportyBet Booking flags
        MapBoolean(
            "SportyBet:EnableBooking",
            "SPORTYBET_ENABLE_BOOKING",
            "ENABLE_BOOKING",
            "ENABLE_SPORTYBET_BOOKING",
            "SPORTYBET__ENABLEBOOKING",
            "SPORTYBET__ENABLE_BOOKING",
            "SportyBet__EnableBooking",
            "SportyBet:EnableBooking");

        MapInt(
            "SportyBet:MaxBookingSelections",
            "SPORTYBET_MAX_BOOKING_SELECTIONS",
            "MAX_BOOKING_SELECTIONS",
            "SPORTYBET__MAXBOOKINGSELECTIONS",
            "SportyBet__MaxBookingSelections",
            "SportyBet:MaxBookingSelections");

        // 2. AdSense Flags
        MapBoolean(
            "AdSense:ReviewSafePublicUi",
            "ADSENSE_REVIEW_SAFE_PUBLIC_UI",
            "ADSENSE_REVIEW_SAFE",
            "REVIEW_SAFE_PUBLIC_UI",
            "REVIEW_SAFE",
            "ADSENSE__REVIEWSAFEPUBLICUI",
            "AdSense__ReviewSafePublicUi",
            "AdSense:ReviewSafePublicUi");

        MapBoolean(
            "AdSense:EnableAdUnits",
            "ADSENSE_ENABLE_AD_UNITS",
            "ENABLE_AD_UNITS",
            "ENABLE_ADSENSE",
            "ADSENSE__ENABLEADUNITS",
            "AdSense__EnableAdUnits",
            "AdSense:EnableAdUnits");

        MapString(
            "AdSense:ClientId",
            "ADSENSE_CLIENT_ID",
            "ADSENSE_PUB_ID",
            "ADSENSE__CLIENTID",
            "AdSense__ClientId",
            "AdSense:ClientId");

        // 3. Admin & Security
        MapString(
            "AdminSettings:SelectionsPasskey",
            "ADMIN_SELECTIONS_PASSKEY",
            "SELECTIONS_PASSKEY",
            "ADMINSETTINGS__SELECTIONSPASSKEY",
            "AdminSettings__SelectionsPasskey",
            "AdminSettings:SelectionsPasskey");

        MapString(
            "Hangfire:Username",
            "HANGFIRE_USERNAME",
            "HANGFIRE__USERNAME",
            "Hangfire__Username",
            "Hangfire:Username");

        MapString(
            "Hangfire:Password",
            "HANGFIRE_PASSWORD",
            "HANGFIRE__PASSWORD",
            "Hangfire__Password",
            "Hangfire:Password");

        MapString(
            "UsageDashboard:Username",
            "USAGE_DASHBOARD_USERNAME",
            "USAGEDASHBOARD__USERNAME",
            "UsageDashboard__Username",
            "UsageDashboard:Username");

        MapString(
            "UsageDashboard:Password",
            "USAGE_DASHBOARD_PASSWORD",
            "USAGEDASHBOARD__PASSWORD",
            "UsageDashboard__Password",
            "UsageDashboard:Password");

        // 4. Runtime Mode Flags
        MapBoolean(
            "RUN_BACKGROUND_JOBS",
            "RUN_BACKGROUND_JOBS",
            "ENABLE_BACKGROUND_JOBS",
            "BACKGROUND_JOBS_ENABLED");

        MapBoolean(
            "ENABLE_BROWSER_SCRAPING",
            "ENABLE_BROWSER_SCRAPING",
            "BROWSER_SCRAPING_ENABLED");

        MapBoolean(
            "ENABLE_USER_TRACKING",
            "ENABLE_USER_TRACKING",
            "USER_TRACKING_ENABLED");

        MapBoolean(
            "USE_EXTERNAL_CRON",
            "USE_EXTERNAL_CRON",
            "EXTERNAL_CRON");

        MapBoolean(
            "SKIP_STARTUP_INITIALIZATION",
            "SKIP_STARTUP_INITIALIZATION",
            "SKIP_STARTUP_INIT");

        // 5. AI Chat & APIs
        MapBoolean(
            "AiChatRequireLogin",
            "AI_CHAT_REQUIRE_LOGIN",
            "REQUIRE_LOGIN_AI_CHAT",
            "AiChatRequireLogin");

        MapString(
            "Social:TwitterHandle",
            "TWITTER_HANDLE",
            "SOCIAL_TWITTER_HANDLE",
            "SOCIAL__TWITTERHANDLE",
            "Social__TwitterHandle",
            "Social:TwitterHandle");

        MapString(
            "ApiFootball:ApiKey",
            "API_FOOTBALL_KEY",
            "APIFOOTBALL_API_KEY",
            "API_FOOTBALL_API_KEY",
            "APIFOOTBALL__APIKEY",
            "ApiFootball__ApiKey",
            "ApiFootball:ApiKey");

        MapString(
            "GroqApiKey",
            "GROQ_API_KEY",
            "GROQ__APIKEY",
            "GroqApiKey");

        // 6. Database Connection (Postgres on Railway)
        var rawDbUrl = Lookup(
            "DATABASE_URL",
            "POSTGRES_URL",
            "DATABASE_PUBLIC_URL",
            "POSTGRESQL_URL",
            "ConnectionStrings__DefaultConnection");

        if (!string.IsNullOrWhiteSpace(rawDbUrl))
        {
            var normalizedCs = ConfigurationFlagExtensions.NormalizePostgresConnectionString(rawDbUrl);
            if (!string.IsNullOrWhiteSpace(normalizedCs))
            {
                normalized["ConnectionStrings:DefaultConnection"] = normalizedCs;
                normalized["ConnectionStrings__DefaultConnection"] = normalizedCs;
                normalized["DATABASE_URL"] = normalizedCs;
            }
        }

        return normalized;
    }
}
