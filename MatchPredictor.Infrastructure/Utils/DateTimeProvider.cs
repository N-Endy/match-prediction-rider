namespace MatchPredictor.Infrastructure.Utils;

public static class DateTimeProvider
{
    private const string WestCentralAfricaTimeZoneId = "W. Central Africa Standard Time";

    // All known date+time formats the system might encounter
    private static readonly string[] DateTimeFormats = new[]
    {
        "dd-MM-yyyy HH:mm",
        "dd-MM-yyyy H:mm",
        "d-M-yyyy HH:mm",
        "d-M-yyyy H:mm",
        "d.M.yyyy HH:mm",
        "d.M.yyyy H:mm",
        "dd/MM/yyyy HH:mm",
        "yyyy-MM-dd HH:mm",
        "MM-dd-yyyy HH:mm",
    };

    private static readonly string[] DateOnlyFormats = new[]
    {
        "dd-MM-yyyy",
        "d-M-yyyy",
        "d.M.yyyy",
        "dd/MM/yyyy",
        "yyyy-MM-dd",
    };

    /// <summary>
    /// Gets the current local time formatted as "dd-MM-yyyy" in West Central Africa Standard Time.
    /// </summary>
    public static string GetLocalTimeString()
    {
        var watZone = TimeZoneInfo.FindSystemTimeZoneById(WestCentralAfricaTimeZoneId);
        var localTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, watZone);
        return localTime.ToString("dd-MM-yyyy");
    }

    /// <summary>
    /// Gets the current local time in West Central Africa Standard Time.
    /// </summary>
    public static DateTime GetLocalTime()
    {
        var watZone = TimeZoneInfo.FindSystemTimeZoneById(WestCentralAfricaTimeZoneId);
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, watZone);
    }

    private static DateTime GetLocalTimeFromUtc(DateTime utcDateTime)
    {
        var watZone = TimeZoneInfo.FindSystemTimeZoneById(WestCentralAfricaTimeZoneId);
        return TimeZoneInfo.ConvertTimeFromUtc(utcDateTime, watZone);
    }
    
    public static DateTime ConvertDateFromString(string dateTimeString)
    {
        if (DateTime.TryParseExact(dateTimeString, DateOnlyFormats, 
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsedDateTime))
        {
            return parsedDateTime;
        }
        
        // Fallback: general parse
        if (DateTime.TryParse(dateTimeString, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out parsedDateTime))
        {
            return GetLocalTimeFromUtc(parsedDateTime);
        }
        throw new FormatException($"Invalid date format: '{dateTimeString}'");
    }

    public static string ConvertTimeToString(DateTime dateTime)
    {
        return GetLocalTimeFromUtc(dateTime).ToString("dd-MMM-yyyy HH:mm:ss");
    }

    public static string ConvertTimeToDateString(DateTime dateTime)
    {
        return GetLocalTimeFromUtc(dateTime).ToString("dd-MM-yyyy");
    }
    
    public static string AddOneHourToTime(string timeString)
    {
        if (DateTime.TryParse(timeString, out var parsedTime))
        {
            var newTime = parsedTime.AddHours(1);
            return newTime.ToString("HH:mm");
        }
        throw new FormatException($"Invalid time format: '{timeString}'. Expected HH:mm.");
    }

    public static (string date, string time) ParseProperDateAndTime(string dateString, string timeString)
    {
        var combined = $"{dateString} {timeString}";
        
        if (DateTime.TryParseExact(combined, DateTimeFormats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsedDateTime))
        {
            var newDateTime = parsedDateTime.AddHours(1);
            return (newDateTime.ToString("dd-MM-yyyy"), newDateTime.ToString("HH:mm"));
        }
        
        // Last resort fallback
        if (DateTime.TryParse(combined, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out parsedDateTime))
        {
            var newDateTime = parsedDateTime.AddHours(1);
            return (newDateTime.ToString("dd-MM-yyyy"), newDateTime.ToString("HH:mm"));
        }
        
        throw new FormatException($"Unable to parse date/time: '{dateString}' + '{timeString}'");
    }
}