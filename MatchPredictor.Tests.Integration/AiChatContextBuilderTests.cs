using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Services;
using MatchPredictor.Infrastructure.Utils;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class AiChatContextBuilderTests
{
    [Fact]
    public void BuildSelection_TreatsMarketOnlyPromptAsMarketFilter_NotMissingFixture()
    {
        var predictions = new[]
        {
            CreatePrediction(1, "BothTeamsScore", "BTTS", "Arsenal", "Chelsea", "England - Premier League", 0.78m),
            CreatePrediction(2, "StraightWin", "Home Win", "Barcelona", "Valencia", "Spain - La Liga", 0.80m),
            CreatePrediction(3, "BothTeamsScore", "BTTS", "Inter", "Milan", "Italy - Serie A", 0.72m)
        };

        var selection = AiChatContextBuilder.BuildSelection(
            predictions,
            "Give me the best BTTS predictions",
            DateTime.UtcNow);

        Assert.False(selection.NoRelevantMatchesFound);
        Assert.Equal(2, selection.Candidates.Count);
        Assert.All(selection.Candidates, candidate => Assert.Equal("BothTeamsScore", candidate.PredictionCategory));
        Assert.Equal(1, selection.Candidates[0].PredictionId);
    }

    [Fact]
    public void BuildSelection_MatchesSpecificTeamAndLeagueQueries()
    {
        var predictions = new[]
        {
            CreatePrediction(1, "StraightWin", "Home Win", "Real Madrid", "Getafe", "Spain - La Liga", 0.77m),
            CreatePrediction(2, "StraightWin", "Home Win", "Manchester City", "Everton", "England - Premier League", 0.79m),
            CreatePrediction(3, "Over2.5Goals", "Over 2.5", "Sevilla", "Villarreal", "Spain - La Liga", 0.69m)
        };

        var selection = AiChatContextBuilder.BuildSelection(
            predictions,
            "What are the best La Liga picks for Real Madrid?",
            DateTime.UtcNow);

        var candidate = Assert.Single(selection.Candidates);
        Assert.Equal(1, candidate.PredictionId);
        Assert.Equal("Real Madrid", candidate.HomeTeam);
    }

    [Fact]
    public void BuildSelection_FlagsUnknownFixtureRequests()
    {
        var predictions = new[]
        {
            CreatePrediction(1, "StraightWin", "Home Win", "Arsenal", "Chelsea", "England - Premier League", 0.78m)
        };

        var selection = AiChatContextBuilder.BuildSelection(
            predictions,
            "Show me Bayern Munich picks",
            DateTime.UtcNow);

        Assert.True(selection.NoRelevantMatchesFound);
        Assert.Empty(selection.Candidates);
        Assert.Equal(1, selection.TotalAvailableCount);
    }

    [Fact]
    public void BuildSelection_ExcludesPastAndNonTodayPredictions()
    {
        var nowUtc = DateTime.UtcNow;
        var today = DateTimeProvider.GetLocalTime().ToString("dd-MM-yyyy");
        var yesterday = DateTimeProvider.GetLocalTime().AddDays(-1).ToString("dd-MM-yyyy");

        var predictions = new[]
        {
            CreatePrediction(1, "StraightWin", "Home Win", "Arsenal", "Chelsea", "England - Premier League", 0.78m, matchDateTimeUtc: nowUtc.AddHours(2), date: today),
            CreatePrediction(2, "Over2.5Goals", "Over 2.5", "Inter", "Milan", "Italy - Serie A", 0.75m, matchDateTimeUtc: nowUtc.AddMinutes(-10), date: today),
            CreatePrediction(3, "Draw", "Draw", "Roma", "Lazio", "Italy - Serie A", 0.32m, matchDateTimeUtc: nowUtc.AddHours(3), date: yesterday)
        };

        var selection = AiChatContextBuilder.BuildSelection(
            predictions,
            "Show me today's picks",
            nowUtc);

        var candidate = Assert.Single(selection.Candidates);
        Assert.Equal(1, candidate.PredictionId);
        Assert.False(selection.NoRelevantMatchesFound);
    }

    [Fact]
    public void BuildSelection_ReturnsRequestedCountsAcrossMultipleMarkets()
    {
        var predictions = Enumerable.Range(1, 25)
            .Select(index => CreatePrediction(index, "BothTeamsScore", "BTTS", $"BTTS Home {index}", $"BTTS Away {index}", "England - Premier League", 0.85m - (index * 0.005m)))
            .Concat(Enumerable.Range(26, 25)
                .Select(index => CreatePrediction(index, "Over2.5Goals", "Over 2.5", $"Over Home {index}", $"Over Away {index}", "Italy - Serie A", 0.83m - ((index - 25) * 0.005m))))
            .Concat(Enumerable.Range(51, 25)
                .Select(index => CreatePrediction(index, "StraightWin", "Home Win", $"Straight Home {index}", $"Straight Away {index}", "Spain - La Liga", 0.81m - ((index - 50) * 0.005m), thresholdUsed: 0.68)))
            .ToArray();

        var selection = AiChatContextBuilder.BuildSelection(
            predictions,
            "Give me 20 btts, 20 over and 20 straightwin and book them",
            DateTime.UtcNow);

        Assert.False(selection.NoRelevantMatchesFound);
        Assert.Equal(60, selection.Candidates.Count);
        Assert.Equal(20, selection.Candidates.Count(candidate => candidate.PredictionCategory == "BothTeamsScore"));
        Assert.Equal(20, selection.Candidates.Count(candidate => candidate.PredictionCategory == "Over2.5Goals"));
        Assert.Equal(20, selection.Candidates.Count(candidate => candidate.PredictionCategory == "StraightWin"));
        Assert.Equal(60, selection.RequestedCandidateCount);
        Assert.Collection(
            selection.RequestedMarketSlices,
            slice => Assert.Equal("BothTeamsScore", slice.PredictionCategory),
            slice => Assert.Equal("Over2.5Goals", slice.PredictionCategory),
            slice => Assert.Equal("StraightWin", slice.PredictionCategory));
    }

    [Fact]
    public void BuildSelection_TreatsStrongPicksForTodayPrompt_AsGenericRecommendationRequest()
    {
        var predictions = new[]
        {
            CreatePrediction(1, "StraightWin", "Home Win", "Palmeiras", "Sporting Cristal", "CONMEBOL - Copa Libertadores U20", 0.905m, thresholdUsed: 0.68),
            CreatePrediction(2, "StraightWin", "Away Win", "Arouca", "Benfica", "Portugal - Primeira Liga", 0.724m, thresholdUsed: 0.70),
            CreatePrediction(3, "BothTeamsScore", "BTTS", "Inter", "Milan", "Italy - Serie A", 0.781m)
        };

        var selection = AiChatContextBuilder.BuildSelection(
            predictions,
            "Give me your strong picks for today",
            DateTime.UtcNow);

        Assert.False(selection.NoRelevantMatchesFound);
        Assert.NotEmpty(selection.Candidates);
    }

    [Fact]
    public void BuildSelection_TreatsRolloverPrompt_AsGenericRecommendationRequest()
    {
        var predictions = new[]
        {
            CreatePrediction(1, "StraightWin", "Home Win", "Palmeiras", "Sporting Cristal", "CONMEBOL - Copa Libertadores U20", 0.905m, thresholdUsed: 0.68),
            CreatePrediction(2, "StraightWin", "Away Win", "Arouca", "Benfica", "Portugal - Primeira Liga", 0.724m, thresholdUsed: 0.70),
            CreatePrediction(3, "BothTeamsScore", "BTTS", "Inter", "Milan", "Italy - Serie A", 0.781m)
        };

        var selection = AiChatContextBuilder.BuildSelection(
            predictions,
            "I'm doing a rollover of 2 odds. Today is day 1. Give me your strong picks for today",
            DateTime.UtcNow);

        Assert.False(selection.NoRelevantMatchesFound);
        Assert.NotEmpty(selection.Candidates);
    }

    [Fact]
    public void BuildSelection_ExtractsRolloverTargetOdds_WhenPresent()
    {
        var predictions = new[]
        {
            CreatePrediction(1, "StraightWin", "Home Win", "Palmeiras", "Sporting Cristal", "CONMEBOL - Copa Libertadores U20", 0.905m, thresholdUsed: 0.68),
            CreatePrediction(2, "BothTeamsScore", "BTTS", "Inter", "Milan", "Italy - Serie A", 0.781m)
        };

        var selection = AiChatContextBuilder.BuildSelection(
            predictions,
            "I'm doing a rollover of 2.5 odds. Give me your strong picks for today",
            DateTime.UtcNow);

        Assert.True(selection.IsRolloverRequest);
        Assert.False(selection.NeedsRolloverTargetOdds);
        Assert.Equal(2.5, selection.RequestedCombinedOdds);
    }

    [Fact]
    public void BuildSelection_UsesGenericPickCount_WhenNoMarketSlicesAreSpecified()
    {
        var predictions = Enumerable.Range(1, 10)
            .Select(index => CreatePrediction(index, "StraightWin", "Home Win", $"Team {index}", $"Opponent {index}", "England - Premier League", 0.82m - (index * 0.01m), thresholdUsed: 0.68))
            .ToArray();

        var selection = AiChatContextBuilder.BuildSelection(
            predictions,
            "Give me 3 strong picks for today",
            DateTime.UtcNow);

        Assert.False(selection.NoRelevantMatchesFound);
        Assert.Equal(3, selection.RequestedCandidateCount);
        Assert.Equal(3, selection.Candidates.Count);
    }

    private static Prediction CreatePrediction(
        int id,
        string category,
        string outcome,
        string homeTeam,
        string awayTeam,
        string league,
        decimal confidence,
        DateTime? matchDateTimeUtc = null,
        string? date = null,
        double thresholdUsed = 0.55)
    {
        var kickoffUtc = matchDateTimeUtc ?? DateTime.UtcNow.AddHours(2);

        return new Prediction
        {
            Id = id,
            Date = date ?? DateTimeProvider.GetLocalTime().ToString("dd-MM-yyyy"),
            Time = DateTimeProvider.ConvertUtcToLocal(kickoffUtc).ToString("HH:mm"),
            MatchDateTime = kickoffUtc,
            League = league,
            HomeTeam = homeTeam,
            AwayTeam = awayTeam,
            PredictionCategory = category,
            PredictedOutcome = outcome,
            ConfidenceScore = confidence,
            RawConfidenceScore = confidence,
            ThresholdUsed = thresholdUsed,
            ThresholdSource = "Configured",
            CalibratorUsed = "Bucket",
            WasPublished = true
        };
    }
}
