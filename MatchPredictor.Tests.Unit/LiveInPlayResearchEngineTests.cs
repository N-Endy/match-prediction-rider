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
        Assert.Contains("pivots tactical focus to Under", analysis.ContrastSummary);
        Assert.NotEmpty(analysis.TacticalReasoning);
        Assert.NotEmpty(analysis.KeyRisk);
        Assert.NotEmpty(analysis.LiveInsightBullets);

        // Portfolio assertions
        Assert.Equal(3, analysis.Recommendations.Count);
        Assert.Contains(analysis.Recommendations, r => r.Tier == "Primary");
        Assert.Contains(analysis.Recommendations, r => r.Tier == "Value");
        Assert.Contains(analysis.Recommendations, r => r.Tier == "Safety");
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
        Assert.Contains("Pre-match model favored Under 2.5", analysis.ContrastSummary);
        Assert.Contains("pivots tactical focus to Over 2.5 Goals", analysis.ContrastSummary);
    }

    [Fact]
    public void Analyze_FirstHalf_ScorelessAt25Min_RecommendsFirstHalfOver05()
    {
        var nowUtc = new DateTime(2026, 10, 25, 15, 25, 0, DateTimeKind.Utc);
        var kickoffUtc = nowUtc.AddMinutes(-25); // 25'

        var candidate = new AiChatContextBuilder.AiChatContextCandidate
        {
            ActionKey = "P110",
            PredictionId = 110,
            MatchLocalDate = new DateOnly(2026, 10, 25),
            MatchDateTimeUtc = kickoffUtc,
            League = "Premier League",
            KickoffTime = "15:00",
            HomeTeam = "Liverpool",
            AwayTeam = "Everton",
            PredictionCategory = "Over2.5Goals",
            PredictedOutcome = "Over 2.5 Goals",
            ActualScore = "0 - 0",
            ConfidenceScore = 0.75m,
            MatchState = "Live",
            CanBook = false,
            FixtureKey = "PL|Liverpool|Everton|15:00",
            SearchTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Liverpool", "Everton" }
        };

        var analysis = _engine.Analyze(candidate, nowUtc);

        Assert.Equal(3, analysis.Recommendations.Count);
        var valueRec = Assert.Single(analysis.Recommendations, r => r.Tier == "Value");
        Assert.Equal("1st Half Goals", valueRec.Market);
        Assert.Equal("Over 0.5 1st Half Goals", valueRec.Outcome);
        Assert.True(valueRec.Confidence >= 0.65);
    }

    [Fact]
    public void Analyze_SecondHalf_ScorelessAtHT_RecommendsSecondHalfOver05()
    {
        var nowUtc = new DateTime(2026, 10, 25, 16, 05, 0, DateTimeKind.Utc);
        var kickoffUtc = nowUtc.AddMinutes(-55); // ~HT / 45'

        var candidate = new AiChatContextBuilder.AiChatContextCandidate
        {
            ActionKey = "P111",
            PredictionId = 111,
            MatchLocalDate = new DateOnly(2026, 10, 25),
            MatchDateTimeUtc = kickoffUtc,
            League = "Serie A",
            KickoffTime = "15:00",
            HomeTeam = "Juventus",
            AwayTeam = "Roma",
            PredictionCategory = "1X2",
            PredictedOutcome = "Juventus",
            ActualScore = "0 - 0",
            ConfidenceScore = 0.66m,
            MatchState = "Live",
            CanBook = false,
            FixtureKey = "SerieA|Juventus|Roma|15:00",
            SearchTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Juventus", "Roma" }
        };

        var analysis = _engine.Analyze(candidate, nowUtc);

        Assert.Equal(3, analysis.Recommendations.Count);
        var valueRec = Assert.Single(analysis.Recommendations, r => r.Tier == "Value");
        Assert.Equal("2nd Half Goals", valueRec.Market);
        Assert.Equal("2nd Half Over 0.5 Goals", valueRec.Outcome);
        Assert.True(valueRec.Confidence >= 0.70);
    }

    [Fact]
    public void Analyze_TrailingFavorite_EvaluatesTeamToScore()
    {
        var nowUtc = new DateTime(2026, 10, 25, 16, 15, 0, DateTimeKind.Utc);
        var kickoffUtc = nowUtc.AddMinutes(-65); // ~50'

        var candidate = new AiChatContextBuilder.AiChatContextCandidate
        {
            ActionKey = "P112",
            PredictionId = 112,
            MatchLocalDate = new DateOnly(2026, 10, 25),
            MatchDateTimeUtc = kickoffUtc,
            League = "Bundesliga",
            KickoffTime = "15:00",
            HomeTeam = "Bayern Munich",
            AwayTeam = "Bochum",
            PredictionCategory = "1X2",
            PredictedOutcome = "Bayern Munich",
            ActualScore = "0 - 1",
            ConfidenceScore = 0.80m,
            MatchState = "Live",
            CanBook = false,
            FixtureKey = "Bundesliga|Bayern|Bochum|15:00",
            SearchTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Bayern Munich", "Bochum" }
        };

        var analysis = _engine.Analyze(candidate, nowUtc);

        Assert.Equal(3, analysis.Recommendations.Count);
        var valueRec = Assert.Single(analysis.Recommendations, r => r.Tier == "Value");
        Assert.Equal("Team to Score", valueRec.Market);
        Assert.Contains("Bayern Munich to Score", valueRec.Outcome);
    }

    [Fact]
    public void CreateInPlayActions_GeneratesThreeTieredActionCards()
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
        var actions = _engine.CreateInPlayActions(candidate, analysis);

        Assert.Equal(3, actions.Count);
        Assert.All(actions, action =>
        {
            Assert.Equal("Live", action.Status);
            Assert.False(action.CanBook);
            Assert.Equal("2 - 0", action.ActualScore);
            Assert.NotEmpty(action.Prediction);
            Assert.NotEmpty(action.Explanation);
        });

        Assert.Contains(actions, a => a.MatchDateLabel.Contains("🎯"));
        Assert.Contains(actions, a => a.MatchDateLabel.Contains("💎"));
        Assert.Contains(actions, a => a.MatchDateLabel.Contains("🛡️"));
    }

    [Fact]
    public void BuildInPlaySummaryMessage_RendersMultiMarketPortfolio()
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
        Assert.Contains("Multi-Market Tactical Portfolio:", message);
        Assert.Contains("🎯 **Primary Best Read:**", message);
        Assert.Contains("💎 **Value / Momentum Angle:**", message);
        Assert.Contains("🛡️ **Safety / Banker Hedge:**", message);
    }
}
