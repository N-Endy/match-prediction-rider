namespace MatchPredictor.Infrastructure.Statistics;

/// <summary>
/// A finished historical match used to train the statistical core
/// (Dixon-Coles goals model and the Elo rating system).
/// </summary>
public sealed record MatchResult(
    string HomeTeam,
    string AwayTeam,
    int HomeGoals,
    int AwayGoals,
    DateTime DateUtc,
    string? League = null);
