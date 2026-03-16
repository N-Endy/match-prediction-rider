namespace MatchPredictor.Domain.Models;

public sealed record FixtureIdentity(
    string FixtureKey,
    string League,
    string HomeTeam,
    string AwayTeam,
    DateOnly? MatchLocalDate,
    TimeOnly? MatchLocalTime,
    DateTime? KickoffTimeUtc);
