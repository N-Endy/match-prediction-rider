namespace MatchPredictor.Domain.Helpers;

/// <summary>
/// Detects fixture rows from the Excel feed that represent unsupported parallel markets
/// (e.g. bookings cards) so they can be excluded from ingestion and user-facing surfaces.
/// </summary>
public static class UnsupportedFixtureFilter
{
    public static bool IsBookingsFixture(string? homeTeam, string? awayTeam) =>
        ContainsBookingsMarker(homeTeam) || ContainsBookingsMarker(awayTeam);

    private static bool ContainsBookingsMarker(string? teamName) =>
        !string.IsNullOrWhiteSpace(teamName) &&
        teamName.Contains("(Bookings)", StringComparison.OrdinalIgnoreCase);
}
