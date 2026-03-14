namespace MatchPredictor.Domain.Models;

public class UsageSnapshot
{
    public int UniqueVisitorsLast24Hours { get; set; }
    public int UniqueVisitorsLast7Days { get; set; }
    public int SessionsLast7Days { get; set; }
    public int PageViewsLast7Days { get; set; }
    public int AiChatRequestsLast7Days { get; set; }
    public int CartAddsLast7Days { get; set; }
    public int BookingAttemptsLast7Days { get; set; }
    public int SuccessfulBookingsLast7Days { get; set; }
    public List<UsagePathSummary> TopPagesLast7Days { get; set; } = [];
}

public class UsagePathSummary
{
    public string Path { get; set; } = string.Empty;
    public int Count { get; set; }
}
