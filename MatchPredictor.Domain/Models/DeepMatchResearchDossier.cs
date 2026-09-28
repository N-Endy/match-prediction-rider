namespace MatchPredictor.Domain.Models;

public class DeepMatchResearchDossier
{
    public string FixtureKey { get; set; } = string.Empty;
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public string League { get; set; } = string.Empty;
    public DateTime? MatchDateUtc { get; set; }

    // Statistical model synthesis
    public double HomeExpectedGoals { get; set; }
    public double AwayExpectedGoals { get; set; }
    public double HomeEloRating { get; set; } = 1500;
    public double AwayEloRating { get; set; } = 1500;
    public double EloDifference => HomeEloRating - AwayEloRating;

    // Market and calibrated probabilities
    public double CalibratedHomeWinProb { get; set; }
    public double CalibratedDrawProb { get; set; }
    public double CalibratedAwayWinProb { get; set; }
    public double CalibratedOver25Prob { get; set; }
    public double CalibratedBttsProb { get; set; }

    public double? MarketHomeOdds { get; set; }
    public double? MarketDrawOdds { get; set; }
    public double? MarketAwayOdds { get; set; }
    public double? MarketOver25Odds { get; set; }
    public double? MarketBttsOdds { get; set; }

    // Edge and Conviction
    public double ValueEdgePoints { get; set; }
    public string ConvictionLevel { get; set; } = "Moderate";
    public string RecommendedPick { get; set; } = string.Empty;

    // Form and H2H
    public FootballMatchInsightSnapshot? FormAndH2H { get; set; }

    // External real-time research context
    public List<string> ConfirmedOrPredictedLineupsHome { get; set; } = [];
    public List<string> ConfirmedOrPredictedLineupsAway { get; set; } = [];
    public List<string> InjuriesAndSuspensionsHome { get; set; } = [];
    public List<string> InjuriesAndSuspensionsAway { get; set; } = [];
    public List<string> TacticalFactors { get; set; } = [];
    public string ResearchSummary { get; set; } = string.Empty;
    public string DataQuality { get; set; } = "High";
    public bool ExternalDataIncluded { get; set; }
    public TimeSpan ResearchLatency { get; set; }
}
