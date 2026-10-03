namespace MatchPredictor.Domain.Models;

/// <summary>
/// A point-in-time capture of confirmed starting lineups, key player absences,
/// and computed attack/defense impact adjustments for a fixture.
/// </summary>
public class MatchLineupSnapshot
{
    public int Id { get; set; }
    public string FixtureKey { get; set; } = string.Empty;
    public DateOnly MatchLocalDate { get; set; }
    public DateTime? MatchDateTimeUtc { get; set; }
    public string League { get; set; } = string.Empty;
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;

    /// <summary>True when official starting XIs have been published (typically ~60m before kickoff).</summary>
    public bool IsConfirmed { get; set; }

    /// <summary>JSON array of confirmed home starting players.</summary>
    public string HomeStartingXiJson { get; set; } = "[]";

    /// <summary>JSON array of confirmed away starting players.</summary>
    public string AwayStartingXiJson { get; set; } = "[]";

    /// <summary>JSON array of notable missing players, injuries, or suspensions for home side.</summary>
    public string HomeAbsencesJson { get; set; } = "[]";

    /// <summary>JSON array of notable missing players, injuries, or suspensions for away side.</summary>
    public string AwayAbsencesJson { get; set; } = "[]";

    /// <summary>Attack adjustment delta to home team log-lambda (e.g. -0.15 for missing top scorer).</summary>
    public double HomeAttackAdjustment { get; set; }

    /// <summary>Defense adjustment delta to home team log-lambda (positive value means degraded defense, conceding more).</summary>
    public double HomeDefenseAdjustment { get; set; }

    /// <summary>Attack adjustment delta to away team log-mu.</summary>
    public double AwayAttackAdjustment { get; set; }

    /// <summary>Defense adjustment delta to away team log-mu (positive value means degraded defense).</summary>
    public double AwayDefenseAdjustment { get; set; }

    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
}
