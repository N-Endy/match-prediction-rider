namespace MatchPredictor.Domain.Models;

public class BookingSelection
{
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public string League { get; set; } = string.Empty;
    public string Market { get; set; } = string.Empty; // "1X2", "BTTS", "Over2.5", "Under2.5"
    public string Prediction { get; set; } = string.Empty; // "Home Win", "BTTS", "Over 2.5", "Under 2.5", etc.
    public int? PredictionId { get; set; }
    public DateTime? MatchDateTimeUtc { get; set; }
}

public class BookingRequest
{
    public List<BookingSelection> Selections { get; set; } = [];
}

public class BookingResult
{
    public bool Success { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public string BookingUrl { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int BookedCount { get; set; }
    public int SkippedCount { get; set; }
    public List<string> Warnings { get; set; } = [];
}
