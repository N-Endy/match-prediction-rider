namespace MatchPredictor.Domain.Models;

public class BookingSelection
{
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public string League { get; set; } = string.Empty;
    public string Market { get; set; } = string.Empty; // "1X2", "BTTS", "Over2.5"
    public string Prediction { get; set; } = string.Empty; // "Home Win", "BTTS", "Over 2.5", etc.
}

public class BookingRequest
{
    public List<BookingSelection> Selections { get; set; } = [];
}

public class BookingResult
{
    public bool Success { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
