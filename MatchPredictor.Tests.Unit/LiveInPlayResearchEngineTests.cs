using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Services;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class LiveInPlayResearchEngineTests
{
    private readonly LiveInPlayResearchEngine _engine = new();

    [Fact]
    public void Analyze_Over25_ScorelessLate_PivotsToUnder()
    {
        var nowUtc = new DateTime(2026, 10, 25, 16, 20, 0, DateTimeKind.Utc);
        var kickoffUtc = nowUtc.AddMinutes(-85); // 70 mins into match (accounting for HT)

        var candidate = new AiChatContextBuilder.AiChatContextCandidate
        {
            ActionKey = "P101",
            PredictionId = 101,
            MatchLocalDate = new DateOnly(2026, 10, 25),
            MatchDateTimeUtc = kickoffUtc,
            League = "Premier League",
            KickoffTime = "15:00",
            HomeTeam = "Arsenal",
            AwayTeam = "Chelsea",
            PredictionCategory = "Over2.5Goals",
            PredictedOutcome = "Over 2.5 Goals",
            ActualScore = "0 - 0",
            ConfidenceScore = 0.72m,
            MatchState = "Live",
            CanBook = false,
            FixtureKey = "PL|Arsenal|Chelsea|15:00",
            SearchTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Arsenal", "Chelsea" }
        };

        var analysis = _engine.Analyze(candidate, nowUtc);

        Assert.Equal("0 - 0", analysis.LiveScore);
        Assert.True(analysis.ElapsedMinutes >= 65);
        Assert.Equal("Over/Under Goals", analysis.InPlayMarket);
        Assert.Contains("Under", analysis.InPlayOutcome);
        Assert.Contains("Pre-match model favored Over 2.5", analysis.ContrastSummary);
        Assert.Contains("pivoting to Under", analysis.ContrastSummary);
        Assert.NotEmpty(analysis.TacticalReasoning);
        Assert.NotEmpty(analysis.KeyRisk);
        Assert.NotEmpty(analysis.LiveInsightBullets);
    }

    [Fact]
    public void Analyze_Under25_EarlyHighScoring_PivotsToOver()
    {
        var nowUtc = new DateTime(2026, 10, 25, 15, 35, 0, DateTimeKind.Utc);
        var kickoffUtc = nowUtc.AddMinutes(-35); // 35' 1st Half

        var candidate = new AiChatContextBuilder.AiChatContextCandidate
        {
            ActionKey = "P102",
            PredictionId = 102,
            MatchLocalDate = new DateOnly(2026, 10, 25),
            MatchDateTimeUtc = kickoffUtc,
            League = "La Liga",
            KickoffTime = "15:00",
            HomeTeam = "Atletico Madrid",
            AwayTeam = "Sevilla",
            PredictionCategory = "Under2.5Goals",
            PredictedOutcome = "Under 2.5 Goals",
            ActualScore = "1 - 1",
            ConfidenceScore = 0.68m,
            MatchState = "Live",
            CanBook = false,
            FixtureKey = "LaLiga|Atletico|Sevilla|15:00",
            SearchTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Atletico", "Sevilla" }
        };

        var analysis = _engine.Analyze(candidate, nowUtc);

        Assert.Equal("1 - 1", analysis.LiveScore);
        Assert.Equal(35, analysis.ElapsedMinutes);
        Assert.Equal("1st Half", analysis.MatchPhase);
        Assert.Equal("Over 2.5 Goals", analysis.InPlayOutcome);
        Assert.Contains("Pre-match model leaned Under 2.5", analysis.ContrastSummary);
        Assert.Contains("pivoting to Over 2.5 Goals", analysis.ContrastSummary);
    }

    [Fact]
    public void Analyze_Btts_BothScored_PivotsToGoalTotal()
    {
        var nowUtc = new DateTime(2026, 10, 25, 16, 10, 0, DateTimeKind.Utc);
        var kickoffUtc = nowUtc.AddMinutes(-70); // ~55'

        var candidate = new AiChatContextBuilder.AiChatContextCandidate
        {
            ActionKey = "P103",
            PredictionId = 103,
            MatchLocalDate = new DateOnly(2026, 10, 25),
            MatchDateTimeUtc = kickoffUtc,
            League = "Bundesliga",
            KickoffTime = "15:00",
            HomeTeam = "Dortmund",
            AwayTeam = "Bayern",
            PredictionCategory = "BothTeamsScore",
            PredictedOutcome = "Yes",
            ActualScore = "1 - 2",
            ConfidenceScore = 0.70m,
            MatchState = "Live",
            CanBook = false,
            FixtureKey = "Bundesliga|Dortmund|Bayern|15:00",
            SearchTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Dortmund", "Bayern" }
        };

        var analysis = _engine.Analyze(candidate, nowUtc);

        Assert.Equal("1 - 2", analysis.LiveScore);
        Assert.Contains("Pre-match BTTS", analysis.ContrastSummary);
        Assert.Contains("already won", analysis.ContrastSummary);
        Assert.Equal("Over 3.5 Goals", analysis.InPlayOutcome);
    }

    [Fact]
    public void Analyze_Btts_OneTeamStifledLate_PivotsToBttsNo()
    {
        var nowUtc = new DateTime(2026, 10, 25, 16, 30, 0, DateTimeKind.Utc);
        var kickoffUtc = nowUtc.AddMinutes(-95); // ~80'

        var candidate = new AiChatContextBuilder.AiChatContextCandidate
        {
            ActionKey = "P104",
            PredictionId = 104,
            MatchLocalDate = new DateOnly(2026, 10, 25),
            MatchDateTimeUtc = kickoffUtc,
            League = "Serie A",
            KickoffTime = "15:00",
            HomeTeam = "Inter",
            AwayTeam = "Milan",
            PredictionCategory = "BothTeamsScore",
            PredictedOutcome = "Yes",
            ActualScore = "1 - 0",
            ConfidenceScore = 0.69m,
            MatchState = "Live",
            CanBook = false,
            FixtureKey = "SerieA|Inter|Milan|15:00",
            SearchTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Inter", "Milan" }
        };

        var analysis = _engine.Analyze(candidate, nowUtc);

        Assert.Equal("1 - 0", analysis.LiveScore);
        Assert.True(analysis.ElapsedMinutes >= 75);
        Assert.Equal("Both Teams To Score", analysis.InPlayMarket);
        Assert.Equal("BTTS: No", analysis.InPlayOutcome);
        Assert.Contains("pivoting to BTTS: No", analysis.ContrastSummary);
    }

    [Fact]
    public void Analyze_1X2_DeadlockLate_PivotsToDraw()
    {
        var nowUtc = new DateTime(2026, 10, 25, 16, 25, 0, DateTimeKind.Utc);
        var kickoffUtc = nowUtc.AddMinutes(-90); // ~75'

        var candidate = new AiChatContextBuilder.AiChatContextCandidate
        {
            ActionKey = "P105",
            PredictionId = 105,
            MatchLocalDate = new DateOnly(2026, 10, 25),
            MatchDateTimeUtc = kickoffUtc,
            League = "Premier League",
            KickoffTime = "15:00",
            HomeTeam = "Liverpool",
            AwayTeam = "Man City",
            PredictionCategory = "1X2",
            PredictedOutcome = "Liverpool",
            ActualScore = "1 - 1",
            ConfidenceScore = 0.62m,
            MatchState = "Live",
            CanBook = false,
            FixtureKey = "PL|Liverpool|ManCity|15:00",
            SearchTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Liverpool", "ManCity" }
        };

        var analysis = _engine.Analyze(candidate, nowUtc);

        Assert.Equal("1 - 1", analysis.LiveScore);
        Assert.Equal("Match Winner / 1X2", analysis.InPlayMarket);
        Assert.Equal("Draw (X)", analysis.InPlayOutcome);
        Assert.Contains("pivots to Draw (X)", analysis.ContrastSummary);
    }

    [Fact]
    public void CreateInPlayAction_SetsStatusLiveAndCanBookFalse()
    {
        var nowUtc = new DateTime(2026, 10, 25, 16, 20, 0, DateTimeKind.Utc);
        var kickoffUtc = nowUtc.AddMinutes(-85);

        var candidate = new AiChatContextBuilder.AiChatContextCandidate
        {
            ActionKey = "P106",
            PredictionId = 106,
            MatchLocalDate = new DateOnly(2026, 10, 25),
            MatchDateTimeUtc = kickoffUtc,
            League = "Premier League",
            KickoffTime = "15:00",
            HomeTeam = "Tottenham",
            AwayTeam = "West Ham",
            PredictionCategory = "1X2",
            PredictedOutcome = "Tottenham",
            ActualScore = "2 - 0",
            ConfidenceScore = 0.70m,
            MatchState = "Live",
            CanBook = false,
            FixtureKey = "PL|Tottenham|WestHam|15:00",
            SearchTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Tottenham", "West Ham" }
        };

        var analysis = _engine.Analyze(candidate, nowUtc);
        var action = _engine.CreateInPlayAction(candidate, analysis);

        Assert.Equal("inplay_pick", action.Type);
        Assert.Equal("Live", action.Status);
        Assert.False(action.CanBook);
        Assert.Equal("2 - 0", action.ActualScore);
        Assert.Equal("Match Winner / 1X2", action.Market);
        Assert.Equal("Tottenham to Win", action.Prediction);
        Assert.Equal(analysis.ContrastSummary, action.AnalysisSummary);
        Assert.NotNull(action.Explanation);
    }

    [Fact]
    public void BuildInPlaySummaryMessage_ReturnsExpectedMarkdownSections()
    {
        var nowUtc = new DateTime(2026, 10, 25, 16, 20, 0, DateTimeKind.Utc);
        var candidate = new AiChatContextBuilder.AiChatContextCandidate
        {
            ActionKey = "P107",
            PredictionId = 107,
            MatchLocalDate = new DateOnly(2026, 10, 25),
            MatchDateTimeUtc = nowUtc.AddMinutes(-70),
            League = "Premier League",
            KickoffTime = "15:00",
            HomeTeam = "Newcastle",
            AwayTeam = "Aston Villa",
            PredictionCategory = "Over2.5Goals",
            PredictedOutcome = "Over 2.5 Goals",
            ActualScore = "0 - 0",
            ConfidenceScore = 0.65m,
            MatchState = "Live",
            CanBook = false,
            FixtureKey = "PL|Newcastle|AstonVilla|15:00",
            SearchTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Newcastle", "Aston Villa" }
        };

        var analysis = _engine.Analyze(candidate, nowUtc);
        var message = _engine.BuildInPlaySummaryMessage([analysis], "What live games can I bet in-play on?");

        Assert.Contains("🔴 Live In-Play Match Research", message);
        Assert.Contains("⚡ Newcastle vs Aston Villa — **0 - 0**", message);
        Assert.Contains("Pre-Match Baseline:", message);
        Assert.Contains("Live In-Play Pivot:", message);
        Assert.Contains("Pre-Match vs. Live Contrast:", message);
        Assert.Contains("Tactical Read:", message);
        Assert.Contains("Key Risk:", message);
    }
}
