namespace MatchPredictor.Infrastructure.Extensions;

public static class DateTimeExtensions
{
    public static (DateTime Start, DateTime End) GetUtcDayBounds(this DateTime date)
    {
        // Forces the date to UTC and calculates the exact start and end
        var startOfDayUtc = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        var endOfDayUtc = startOfDayUtc.AddDays(1);
        
        return (startOfDayUtc, endOfDayUtc);
    }
}