namespace MatchPredictor.Domain.Models;

public class FootballMatchInsightSnapshot
{
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public string InsightSource { get; set; } = "Unavailable";
    public string DataQuality { get; set; } = "Low";
    public bool IsLowConfidence { get; set; }
    public TeamFormSnapshot HomeForm { get; set; } = new();
    public TeamFormSnapshot AwayForm { get; set; } = new();
    public HeadToHeadSummary? HeadToHead { get; set; }
}

public class TeamFormSnapshot
{
    public string TeamName { get; set; } = string.Empty;
    public int SampleSize { get; set; }
    public int VenueSampleSize { get; set; }
    public int Wins { get; set; }
    public int Draws { get; set; }
    public int Losses { get; set; }
    public int VenueWins { get; set; }
    public int VenueDraws { get; set; }
    public int VenueLosses { get; set; }
    public double DrawRate { get; set; }
    public double VenueDrawRate { get; set; }
    public double PointsPerMatch { get; set; }
    public double VenuePointsPerMatch { get; set; }
    public double GoalsForPerMatch { get; set; }
    public double GoalsAgainstPerMatch { get; set; }
    public double VenueGoalsForPerMatch { get; set; }
    public double VenueGoalsAgainstPerMatch { get; set; }
    public double BttsRate { get; set; }
    public double Over25Rate { get; set; }
    public double Under25Rate { get; set; }
    public double CleanSheetRate { get; set; }
    public List<TeamFormMatchSummary> LastFiveOverallResults { get; set; } = [];
    public List<TeamFormMatchSummary> LastFiveVenueResults { get; set; } = [];
}

public class TeamFormMatchSummary
{
    public string MatchDate { get; set; } = string.Empty;
    public string League { get; set; } = string.Empty;
    public string Venue { get; set; } = string.Empty;
    public string Opponent { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public string Score { get; set; } = string.Empty;
}

public class HeadToHeadSummary
{
    public int SampleSize { get; set; }
    public int HomeTeamWins { get; set; }
    public int AwayTeamWins { get; set; }
    public int Draws { get; set; }
    public double BttsRate { get; set; }
    public double Over25Rate { get; set; }
    public List<string> RecentScores { get; set; } = [];
}
