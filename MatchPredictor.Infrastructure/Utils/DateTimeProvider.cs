namespace MatchPredictor.Infrastructure.Utils;

public static class DateTimeProvider
{
    private const string WestCentralAfricaTimeZoneId = "W. Central Africa Standard Time";

    /// <summary>
    /// Gets the current local time formatted as "dd-MMM-yyyy HH:mm:ss" in West Central Africa Standard Time.
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
        if (DateTime.TryParse(dateTimeString, out var parsedDateTime))
        {
            return GetLocalTimeFromUtc(parsedDateTime);
        }
        throw new FormatException("Invalid date time format.");
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
        // Time string is in format 09:00
        if (DateTime.TryParse(timeString, out var parsedTime))
        {
            var newTime = parsedTime.AddHours(1);
            return newTime.ToString("HH:mm");
        }
        throw new FormatException("Invalid time format. Expected format is HH:mm.");
    }

    public static (string date, string time) ParseProperDateAndTime(string dateString, string timeString)
    {
        // Use ParseExact to enforce dd-MM-yyyy format and avoid ambiguous date parsing
        var dateTimeString = $"{dateString} {timeString}";
        if (DateTime.TryParseExact(dateTimeString, "dd-MM-yyyy HH:mm",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var parsedDateTime))
        {
            var newDateTime = parsedDateTime.AddHours(1);
            return (newDateTime.ToString("dd-MM-yyyy"), newDateTime.ToString("HH:mm"));
        }
        throw new FormatException($"Invalid date or time format: {dateTimeString}. Expected format: dd-MM-yyyy HH:mm");
    }

}