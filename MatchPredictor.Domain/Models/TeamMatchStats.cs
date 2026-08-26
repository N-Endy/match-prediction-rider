namespace MatchPredictor.Domain.Models;

/// <summary>
/// One row per team per finished match. Aggregated stats for feature building —
/// not an event store. ExpectedGoals* stay null until a licensed current-season
/// xG source is wired (SofaScore ingest currently has no xG fields).
/// </summary>
public class TeamMatchStats
{
    public long Id { get; set; }
    public int? TeamId { get; set; }
    public int? OpponentTeamId { get; set; }
    public DateTime KickoffUtc { get; set; }
    public DateOnly MatchLocalDate { get; set; }
    public string LeagueKey { get; set; } = string.Empty;
    public string? FixtureKey { get; set; }
    public bool IsHome { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public string OpponentName { get; set; } = string.Empty;
    public string League { get; set; } = string.Empty;
    public short? GoalsFor { get; set; }
    public short? GoalsAgainst { get; set; }
    public float? ExpectedGoalsFor { get; set; }
    public float? ExpectedGoalsAgainst { get; set; }
    public short? Shots { get; set; }
    public short? ShotsOnTarget { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public string? SourceMatchId { get; set; }
    public DateTime ObservedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime AvailableFromUtc { get; set; }
    public bool IsRetroactive { get; set; }
}
