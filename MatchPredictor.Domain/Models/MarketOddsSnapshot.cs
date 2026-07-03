namespace MatchPredictor.Domain.Models;

/// <summary>
/// A point-in-time capture of real bookmaker pricing for one fixture. Unlike
/// <see cref="PredictionOddsSnapshot"/> (which is tied to a published prediction),
/// these rows are captured for every fixture the pricing source quotes, so they can
/// be used as an honest, point-in-time-safe market feature for model training and
/// as the true "market" signal in the ensemble.
/// </summary>
public class MarketOddsSnapshot
{
    public int Id { get; set; }
    public string FixtureKey { get; set; } = string.Empty;
    public DateOnly MatchLocalDate { get; set; }

    /// <summary>Kickoff instant in UTC as reported by the pricing source.</summary>
    public DateTime? MatchDateTimeUtc { get; set; }
    public string League { get; set; } = string.Empty;
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;

    /// <summary>Pricing source, e.g. "SportyBet".</summary>
    public string SourceName { get; set; } = string.Empty;

    // Raw decimal odds as quoted (including the bookmaker margin).
    public double? HomeWinOdds { get; set; }
    public double? DrawOdds { get; set; }
    public double? AwayWinOdds { get; set; }
    public double? Over25Odds { get; set; }
    public double? Under25Odds { get; set; }
    public double? BttsYesOdds { get; set; }
    public double? BttsNoOdds { get; set; }

    // De-vigged fair probabilities (margin removed; see DeVigMethod).
    public double? FairHomeWin { get; set; }
    public double? FairDraw { get; set; }
    public double? FairAwayWin { get; set; }
    public double? FairOver25 { get; set; }
    public double? FairUnder25 { get; set; }
    public double? FairBttsYes { get; set; }
    public double? FairBttsNo { get; set; }

    /// <summary>De-vig method used, e.g. "Shin+Power" or "Proportional" (fallback).</summary>
    public string DeVigMethod { get; set; } = string.Empty;

    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
}
