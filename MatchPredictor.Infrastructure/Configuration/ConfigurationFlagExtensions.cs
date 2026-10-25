using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace MatchPredictor.Infrastructure.Configuration;

/// <summary>
/// Provides resilient flag and setting resolution from IConfiguration,
/// supporting standard .NET hierarchical keys and common Railway/cloud environment variable aliases.
/// </summary>
public static class ConfigurationFlagExtensions
{
    /// <summary>
    /// Parses common truthy/falsy strings into a boolean.
    /// Handles "1"/"0", "yes"/"no", "on"/"off", "true"/"false" (case-insensitive).
    /// </summary>
    public static bool TryParseBoolean(string? rawValue, out bool result)
    {
        result = false;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        var clean = rawValue.Trim().ToLowerInvariant();
        switch (clean)
        {
            case "1":
            case "true":
            case "t":
            case "yes":
            case "y":
            case "on":
                result = true;
                return true;
            case "0":
            case "false":
            case "f":
            case "no":
            case "n":
            case "off":
                result = false;
                return true;
            default:
                return bool.TryParse(clean, out result);
        }
    }

    /// <summary>
    /// Resolves a boolean flag, checking the primary key first, followed by any aliases.
    /// </summary>
    public static bool GetBooleanFlag(
        this IConfiguration configuration,
        string primaryKey,
        bool defaultValue,
        params string[] aliases)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // 1. Check primary key first
        var primaryRaw = configuration[primaryKey];
        if (!string.IsNullOrWhiteSpace(primaryRaw) && TryParseBoolean(primaryRaw, out var primaryParsed))
        {
            return primaryParsed;
        }

        // 2. Check aliases in priority order
        if (aliases != null)
        {
            foreach (var alias in aliases)
            {
                if (string.IsNullOrWhiteSpace(alias))
                {
                    continue;
                }

                var aliasRaw = configuration[alias];
                if (!string.IsNullOrWhiteSpace(aliasRaw) && TryParseBoolean(aliasRaw, out var aliasParsed))
                {
                    return aliasParsed;
                }
            }
        }

        return defaultValue;
    }

    /// <summary>
    /// Resolves an integer flag, checking the primary key first, followed by any aliases.
    /// </summary>
    public static int GetIntFlag(
        this IConfiguration configuration,
        string primaryKey,
        int defaultValue,
        params string[] aliases)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var primaryRaw = configuration[primaryKey];
        if (!string.IsNullOrWhiteSpace(primaryRaw) &&
            int.TryParse(primaryRaw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var primaryInt))
        {
            return primaryInt;
        }

        if (aliases != null)
        {
            foreach (var alias in aliases)
            {
                if (string.IsNullOrWhiteSpace(alias))
                {
                    continue;
                }

                var aliasRaw = configuration[alias];
                if (!string.IsNullOrWhiteSpace(aliasRaw) &&
                    int.TryParse(aliasRaw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var aliasInt))
                {
                    return aliasInt;
                }
            }
        }

        return defaultValue;
    }

    /// <summary>
    /// Resolves a string configuration value, checking the primary key first, followed by any aliases.
    /// </summary>
    public static string? GetStringFlag(
        this IConfiguration configuration,
        string primaryKey,
        string? defaultValue = null,
        params string[] aliases)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var primaryRaw = configuration[primaryKey];
        if (!string.IsNullOrWhiteSpace(primaryRaw))
        {
            return primaryRaw.Trim();
        }

        if (aliases != null)
        {
            foreach (var alias in aliases)
            {
                if (string.IsNullOrWhiteSpace(alias))
                {
                    continue;
                }

                var aliasRaw = configuration[alias];
                if (!string.IsNullOrWhiteSpace(aliasRaw))
                {
                    return aliasRaw.Trim();
                }
            }
        }

        return defaultValue;
    }

    /// <summary>
    /// Checks whether SportyBet booking code generation is enabled.
    /// Supports Railway environment variables: SPORTYBET_ENABLE_BOOKING, ENABLE_BOOKING, SportyBet__EnableBooking.
    /// </summary>
    public static bool IsBookingEnabled(this IConfiguration configuration, bool defaultValue = false)
    {
        return configuration.GetBooleanFlag(
            "SportyBet:EnableBooking",
            defaultValue,
            "SPORTYBET_ENABLE_BOOKING",
            "ENABLE_BOOKING",
            "ENABLE_SPORTYBET_BOOKING",
            "SPORTYBET__ENABLEBOOKING",
            "SPORTYBET__ENABLE_BOOKING",
            "SportyBet__EnableBooking");
    }

    /// <summary>
    /// Checks whether AdSense review-safe public UI mode is enabled.
    /// Supports Railway environment variables: ADSENSE_REVIEW_SAFE_PUBLIC_UI, ADSENSE_REVIEW_SAFE, REVIEW_SAFE_PUBLIC_UI, REVIEW_SAFE.
    /// </summary>
    public static bool IsReviewSafePublicUi(this IConfiguration configuration)
    {
        return configuration.GetBooleanFlag(
            "AdSense:ReviewSafePublicUi",
            defaultValue: true,
            "ADSENSE_REVIEW_SAFE_PUBLIC_UI",
            "ADSENSE_REVIEW_SAFE",
            "REVIEW_SAFE_PUBLIC_UI",
            "REVIEW_SAFE",
            "ADSENSE__REVIEWSAFEPUBLICUI",
            "AdSense__ReviewSafePublicUi");
    }

    /// <summary>
    /// Checks whether AdSense ad units are enabled for rendering.
    /// Supports Railway environment variables: ADSENSE_ENABLE_AD_UNITS, ENABLE_AD_UNITS, ENABLE_ADSENSE.
    /// </summary>
    public static bool AreAdUnitsEnabled(this IConfiguration configuration)
    {
        return configuration.GetBooleanFlag(
            "AdSense:EnableAdUnits",
            defaultValue: true,
            "ADSENSE_ENABLE_AD_UNITS",
            "ENABLE_AD_UNITS",
            "ENABLE_ADSENSE",
            "ADSENSE__ENABLEADUNITS",
            "AdSense__EnableAdUnits");
    }

    /// <summary>
    /// Gets the maximum allowed selections for SportyBet booking slip generation.
    /// Supports Railway environment variables: SPORTYBET_MAX_BOOKING_SELECTIONS, MAX_BOOKING_SELECTIONS.
    /// </summary>
    public static int GetMaxBookingSelections(this IConfiguration configuration, int defaultValue = 50)
    {
        var result = configuration.GetIntFlag(
            "SportyBet:MaxBookingSelections",
            defaultValue,
            "SPORTYBET_MAX_BOOKING_SELECTIONS",
            "MAX_BOOKING_SELECTIONS",
            "SPORTYBET__MAXBOOKINGSELECTIONS",
            "SportyBet__MaxBookingSelections");

        return result > 0 ? result : defaultValue;
    }

    /// <summary>
    /// Resolves the owner-only private selections passkey.
    /// Supports Railway environment variables: ADMIN_SELECTIONS_PASSKEY, SELECTIONS_PASSKEY.
    /// </summary>
    public static string? GetSelectionsPasskey(this IConfiguration configuration)
    {
        return configuration.GetStringFlag(
            "AdminSettings:SelectionsPasskey",
            defaultValue: null,
            "ADMIN_SELECTIONS_PASSKEY",
            "SELECTIONS_PASSKEY",
            "ADMINSETTINGS__SELECTIONSPASSKEY",
            "AdminSettings__SelectionsPasskey");
    }

    /// <summary>
    /// Resolves Google AdSense Client ID (ca-pub-...).
    /// Supports Railway environment variables: ADSENSE_CLIENT_ID, ADSENSE_PUB_ID.
    /// </summary>
    public static string? GetAdSenseClientId(this IConfiguration configuration)
    {
        return configuration.GetStringFlag(
            "AdSense:ClientId",
            defaultValue: null,
            "ADSENSE_CLIENT_ID",
            "ADSENSE_PUB_ID",
            "ADSENSE__CLIENTID",
            "AdSense__ClientId");
    }

    /// <summary>
    /// Resolves social Twitter/X handle.
    /// Supports Railway environment variables: TWITTER_HANDLE, SOCIAL_TWITTER_HANDLE.
    /// </summary>
    public static string? GetSocialTwitterHandle(this IConfiguration configuration)
    {
        return configuration.GetStringFlag(
            "Social:TwitterHandle",
            defaultValue: null,
            "TWITTER_HANDLE",
            "SOCIAL_TWITTER_HANDLE",
            "SOCIAL__TWITTERHANDLE",
            "Social__TwitterHandle");
    }

    /// <summary>
    /// Converts a PostgreSQL URI (e.g. Railway's postgresql://user:pass@host:port/db)
    /// into a valid Npgsql connection string format.
    /// If the input is already a standard key-value connection string, returns it trimmed.
    /// </summary>
    public static string? NormalizePostgresConnectionString(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var trimmed = rawValue.Trim();
        if (!trimmed.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        try
        {
            var uri = new Uri(trimmed);
            var userInfo = uri.UserInfo.Split(':', 2);
            var username = userInfo.Length > 0 ? Uri.UnescapeDataString(userInfo[0]) : "";
            var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
            var port = uri.Port > 0 ? uri.Port : 5432;
            var database = uri.AbsolutePath.TrimStart('/');

            var builder = new StringBuilder();
            builder.Append($"Host={uri.Host};");
            builder.Append($"Port={port};");
            if (!string.IsNullOrEmpty(database))
            {
                builder.Append($"Database={database};");
            }
            if (!string.IsNullOrEmpty(username))
            {
                builder.Append($"Username={username};");
            }
            if (!string.IsNullOrEmpty(password))
            {
                builder.Append($"Password={password};");
            }
            builder.Append("SSL Mode=Require;Trust Server Certificate=true;");

            return builder.ToString();
        }
        catch
        {
            return trimmed;
        }
    }
}
