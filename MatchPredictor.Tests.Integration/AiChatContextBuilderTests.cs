using System.Globalization;
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
    public void BuildSelection_IgnoresNamedTeams_OnPickListPrompts()
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

        Assert.False(selection.NoRelevantMatchesFound);
        Assert.Equal(AiChatIntent.RecommendPicks, selection.NormalizedRequest?.Intent);
        Assert.Empty(selection.NormalizedRequest?.EntityTerms ?? []);
        Assert.Equal(3, selection.Candidates.Count);
        Assert.Contains(selection.Candidates, candidate => candidate.HomeTeam == "Real Madrid");
        Assert.Contains(selection.Candidates, candidate => candidate.HomeTeam == "Manchester City");
    }

    [Fact]
    public void BuildSelection_DoesNotNoMatch_WhenPickPromptHasUnknownTeamWords()
    {
        var predictions = new[]
        {
            CreatePrediction(1, "StraightWin", "Home Win", "Arsenal", "Chelsea", "England - Premier League", 0.78m)
        };

        var selection = AiChatContextBuilder.BuildSelection(
            predictions,
            "Show me Bayern Munich picks",
            DateTime.UtcNow);

        Assert.False(selection.NoRelevantMatchesFound);
        Assert.Equal(AiChatIntent.RecommendPicks, selection.NormalizedRequest?.Intent);
        Assert.Empty(selection.NormalizedRequest?.EntityTerms ?? []);
        var candidate = Assert.Single(selection.Candidates);
        Assert.Equal(1, candidate.PredictionId);
        Assert.Equal(1, selection.TotalAvailableCount);
    }

    [Fact]
    public void BuildSelection_FlagsUnknownFixture_ForMatchDiscussionOfMissingTeam()
    {
        var predictions = new[]
        {
            CreatePrediction(1, "StraightWin", "Home Win", "Arsenal", "Chelsea", "England - Premier League", 0.78m)
        };

        var selection = AiChatContextBuilder.BuildSelection(
            predictions,
            "Tell me about Bayern Munich",
            DateTime.UtcNow);

        Assert.True(selection.NoRelevantMatchesFound);
        Assert.Empty(selection.Candidates);
        Assert.Equal(AiChatIntent.MatchDiscussion, selection.NormalizedRequest?.Intent);
        Assert.Contains(selection.NormalizedRequest?.EntityTerms ?? [], term => term.Contains("bayern", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, selection.TotalAvailableCount);
    }

    [Fact]
    public void BuildSelection_DiscussesNamedTeam_WhenOnTodaysCard()
    {
        var predictions = new[]
        {
            CreatePrediction(1, "StraightWin", "Home Win", "Arsenal", "Chelsea", "England - Premier League", 0.78m),
            CreatePrediction(2, "Over2.5Goals", "Over 2.5", "Inter", "Milan", "Italy - Serie A", 0.75m)
        };

        var selection = AiChatContextBuilder.BuildSelection(
            predictions,
            "Tell me about Arsenal",
            DateTime.UtcNow);

        Assert.False(selection.NoRelevantMatchesFound);
        Assert.Equal(AiChatIntent.MatchDiscussion, selection.NormalizedRequest?.Intent);
        var candidate = Assert.Single(selection.Candidates);
        Assert.Equal(1, candidate.PredictionId);
        Assert.Equal("Arsenal", candidate.HomeTeam);
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
    public void BuildSelection_TreatsDrawRecommendationPrompt_AsGenericMarketRequest()
    {
        var predictions = new[]
        {
            CreatePrediction(1, "Draw", "Draw", "Roma", "Lazio", "Italy - Serie A", 0.34m, thresholdUsed: 0.30),
            CreatePrediction(2, "Draw", "Draw", "Real Sociedad", "Valencia", "Spain - La Liga", 0.33m, thresholdUsed: 0.30),
            CreatePrediction(3, "StraightWin", "Home Win", "Arsenal", "Chelsea", "England - Premier League", 0.78m, thresholdUsed: 0.68)
        };

        var selection = AiChatContextBuilder.BuildSelection(
            predictions,
            "Which draw games would you recommend?",
            DateTime.UtcNow);

        Assert.False(selection.NoRelevantMatchesFound);
        Assert.Equal(2, selection.Candidates.Count);
        Assert.All(selection.Candidates, candidate => Assert.Equal("Draw", candidate.PredictionCategory));
    }

    [Fact]
    public void BuildSelection_ReturnsMixedMarketRecommendations_WhenPromptAsksForMixture()
    {
        var predictions = new[]
        {
            CreatePrediction(1, "BothTeamsScore", "BTTS", "Inter", "Milan", "Italy - Serie A", 0.79m),
            CreatePrediction(2, "Over2.5Goals", "Over 2.5", "Barcelona", "Atletico Madrid", "Spain - La Liga", 0.77m),
            CreatePrediction(3, "StraightWin", "Home Win", "Arsenal", "Chelsea", "England - Premier League", 0.81m, thresholdUsed: 0.68),
            CreatePrediction(4, "Draw", "Draw", "Roma", "Lazio", "Italy - Serie A", 0.32m, thresholdUsed: 0.30)
        };

        var selection = AiChatContextBuilder.BuildSelection(
            predictions,
            "Give me a mixture of btts, over 2.5 and straight win for today",
            DateTime.UtcNow);

        Assert.False(selection.NoRelevantMatchesFound);
        Assert.Contains(selection.Candidates, candidate => candidate.PredictionCategory == "BothTeamsScore");
        Assert.Contains(selection.Candidates, candidate => candidate.PredictionCategory == "Over2.5Goals");
        Assert.Contains(selection.Candidates, candidate => candidate.PredictionCategory == "StraightWin");
    }

    [Fact]
    public void BuildSelection_ReturnsAllNamedMarkets_ForGenericOverUnderGgAndWinMix()
    {
        var predictions = new[]
        {
            CreatePrediction(1, "BothTeamsScore", "BTTS", "Inter", "Milan", "Italy - Serie A", 0.79m),
            CreatePrediction(2, "Over2.5Goals", "Over 2.5", "Barcelona", "Atletico Madrid", "Spain - La Liga", 0.77m),
            CreatePrediction(3, "Under2.5Goals", "Under 2.5", "Getafe", "Osasuna", "Spain - La Liga", 0.76m),
            CreatePrediction(4, "StraightWin", "Home Win", "Arsenal", "Chelsea", "England - Premier League", 0.81m, thresholdUsed: 0.68)
        };

        var selection = AiChatContextBuilder.BuildSelection(
            predictions,
            "Give me a mixture of over, under, gg, and win. Total 8",
            DateTime.UtcNow);

        Assert.False(selection.NoRelevantMatchesFound);
        Assert.Contains(selection.Candidates, candidate => candidate.PredictionCategory == "BothTeamsScore");
        Assert.Contains(selection.Candidates, candidate => candidate.PredictionCategory == "Over2.5Goals");
        Assert.Contains(selection.Candidates, candidate => candidate.PredictionCategory == "Under2.5Goals");
        Assert.Contains(selection.Candidates, candidate => candidate.PredictionCategory == "StraightWin");
    }

    [Fact]
    public void BuildSelection_ReturnsRequestedCount_ForGenericPredictionRequests()
    {
        var predictions = Enumerable.Range(1, 10)
            .Select(index => CreatePrediction(
                index,
                "StraightWin",
                index % 2 == 0 ? "Home Win" : "Away Win",
                $"Home {index}",
                $"Away {index}",
                "England - Premier League",
                0.90m - (index * 0.01m),
                thresholdUsed: 0.68))
            .ToArray();

        var selection = AiChatContextBuilder.BuildSelection(
            predictions,
            "Give me 7 predictions for today",
            DateTime.UtcNow);

        Assert.False(selection.NoRelevantMatchesFound);
        Assert.Equal(10, selection.Candidates.Count);
        Assert.Equal(7, selection.RequestedCandidateCount);
    }

    [Theory]
    [InlineData("You are allowed to select only 7 predictions for me. Which games would you select?", 7)]
    [InlineData("Give me 7 winnable predictions", 7)]
    [InlineData("Select 7 predictions from today's card", 7)]
    public void BuildSelection_UsesTodaysCard_ForConversationalPickPromptsWithCount(string prompt, int expectedCount)
    {
        var predictions = Enumerable.Range(1, 10)
            .Select(index => CreatePrediction(
                index,
                "StraightWin",
                index % 2 == 0 ? "Home Win" : "Away Win",
                $"Home {index}",
                $"Away {index}",
                "England - Premier League",
                0.90m - (index * 0.01m),
                thresholdUsed: 0.68))
            .ToArray();

        var selection = AiChatContextBuilder.BuildSelection(
            predictions,
            prompt,
            DateTime.UtcNow);

        Assert.False(selection.NoRelevantMatchesFound);
        Assert.Equal(AiChatIntent.RecommendPicks, selection.NormalizedRequest?.Intent);
        Assert.Empty(selection.NormalizedRequest?.EntityTerms ?? []);
        Assert.Equal(expectedCount, selection.NormalizedRequest?.RequestedTotalCount);
        Assert.Equal(10, selection.Candidates.Count);
        Assert.Equal(expectedCount, selection.RequestedCandidateCount);
    }

    [Fact]
    public void BuildSelection_SendsFullCard_WhenUserAsksForFifteenRecommendations()
    {
        var predictions = Enumerable.Range(1, 20)
            .Select(index => CreatePrediction(
                index,
                "StraightWin",
                index % 2 == 0 ? "Home Win" : "Away Win",
                $"Home {index}",
                $"Away {index}",
                "England - Premier League",
                0.90m - (index * 0.01m),
                thresholdUsed: 0.68))
            .ToArray();

        var selection = AiChatContextBuilder.BuildSelection(
            predictions,
            "Give me 15 recommendations",
            DateTime.UtcNow);

        Assert.False(selection.NoRelevantMatchesFound);
        Assert.Equal(15, selection.NormalizedRequest?.RequestedTotalCount);
        Assert.Equal(20, selection.Candidates.Count);
        Assert.Equal(15, selection.RequestedCandidateCount);
    }

    [Fact]
    public void BuildSelection_UsesTodaysCard_ForChooseTheBestConversationalPrompt()
    {
        var predictions = Enumerable.Range(1, 10)
            .Select(index => CreatePrediction(
                index,
                "StraightWin",
                index % 2 == 0 ? "Home Win" : "Away Win",
                $"Home {index}",
                $"Away {index}",
                "England - Premier League",
                0.90m - (index * 0.01m),
                thresholdUsed: 0.68))
            .ToArray();

        var selection = AiChatContextBuilder.BuildSelection(
            predictions,
            "If you have to choose the best predictions for me, which games would you select?",
            DateTime.UtcNow);

        Assert.False(selection.NoRelevantMatchesFound);
        Assert.Equal(AiChatIntent.RecommendPicks, selection.NormalizedRequest?.Intent);
        Assert.Empty(selection.NormalizedRequest?.EntityTerms ?? []);
        Assert.Null(selection.NormalizedRequest?.RequestedTotalCount);
        Assert.Equal(10, selection.Candidates.Count);
        Assert.Equal(0, selection.RequestedCandidateCount);
        Assert.All(selection.Candidates, candidate => Assert.StartsWith("Home ", candidate.HomeTeam));
    }

    [Fact]
    public void BuildSelection_UsesStableRandomOrdering_WhenPromptRequestsRandomPicks()
    {
        var nowUtc = DateTime.UtcNow;
        var predictions = Enumerable.Range(1, 8)
            .Select(index => CreatePrediction(
                index,
                "StraightWin",
                index % 2 == 0 ? "Home Win" : "Away Win",
                $"Home {index}",
                $"Away {index}",
                "England - Premier League",
                0.94m - (index * 0.03m),
                thresholdUsed: 0.68))
            .ToArray();

        var firstSelection = AiChatContextBuilder.BuildSelection(
            predictions,
            "Give me 4 random straight wins for today",
            nowUtc);
        var secondSelection = AiChatContextBuilder.BuildSelection(
            predictions,
            "Give me 4 random straight wins for today",
            nowUtc);

        Assert.False(firstSelection.NoRelevantMatchesFound);
        Assert.Equal(4, firstSelection.Candidates.Count);
        Assert.All(firstSelection.Candidates, candidate => Assert.Equal("StraightWin", candidate.PredictionCategory));
        Assert.Equal(firstSelection.Candidates.Select(candidate => candidate.PredictionId), secondSelection.Candidates.Select(candidate => candidate.PredictionId));
        Assert.NotEqual(new[] { 1, 2, 3, 4 }, firstSelection.Candidates.Select(candidate => candidate.PredictionId).ToArray());
    }

    [Fact]
    public void BuildSelection_ReturnsRequestedRandomMixedTotalAcrossAllowedMarkets()
    {
        var predictions = Enumerable.Range(1, 8)
            .Select(index => CreatePrediction(index, "Over2.5Goals", "Over 2.5", $"Over Home {index}", $"Over Away {index}", "Spain - La Liga", 0.90m - (index * 0.01m)))
            .Concat(Enumerable.Range(9, 8)
                .Select(index => CreatePrediction(index, "Under2.5Goals", "Under 2.5", $"Under Home {index}", $"Under Away {index}", "Italy - Serie A", 0.88m - ((index - 8) * 0.01m))))
            .Concat(Enumerable.Range(17, 8)
                .Select(index => CreatePrediction(index, "StraightWin", "Home Win", $"Win Home {index}", $"Win Away {index}", "England - Premier League", 0.86m - ((index - 16) * 0.01m), thresholdUsed: 0.68)))
            .ToArray();

        var selection = AiChatContextBuilder.BuildSelection(
            predictions,
            "Give me random over, under and straight win. Total 9",
            DateTime.UtcNow);

        Assert.False(selection.NoRelevantMatchesFound);
        Assert.Equal(9, selection.Candidates.Count);
        Assert.Equal(9, selection.RequestedCandidateCount);
        Assert.All(selection.Candidates, candidate =>
            Assert.Contains(candidate.PredictionCategory, new[] { "Over2.5Goals", "Under2.5Goals", "StraightWin" }));
        Assert.Contains(selection.Candidates, candidate => candidate.PredictionCategory == "Over2.5Goals");
        Assert.Contains(selection.Candidates, candidate => candidate.PredictionCategory == "Under2.5Goals");
        Assert.Contains(selection.Candidates, candidate => candidate.PredictionCategory == "StraightWin");
    }

    [Fact]
    public void BuildSelection_DefaultsMixtureWithoutCounts_ToTwoPerNamedMarket()
    {
        var predictions = Enumerable.Range(1, 3)
            .Select(index => CreatePrediction(index, "BothTeamsScore", "BTTS", $"BTTS Home {index}", $"BTTS Away {index}", "Italy - Serie A", 0.82m - (index * 0.01m)))
            .Concat(Enumerable.Range(4, 3)
                .Select(index => CreatePrediction(index, "Over2.5Goals", "Over 2.5", $"Over Home {index}", $"Over Away {index}", "Spain - La Liga", 0.81m - ((index - 3) * 0.01m))))
            .Concat(Enumerable.Range(7, 3)
                .Select(index => CreatePrediction(index, "StraightWin", "Home Win", $"Win Home {index}", $"Win Away {index}", "England - Premier League", 0.84m - ((index - 6) * 0.01m), thresholdUsed: 0.68)))
            .ToArray();

        var selection = AiChatContextBuilder.BuildSelection(
            predictions,
            "Give me a mixture of btts, over 2.5 and straight win",
            DateTime.UtcNow);

        Assert.Equal(6, selection.Candidates.Count);
        Assert.Equal(2, selection.Candidates.Count(candidate => candidate.PredictionCategory == "BothTeamsScore"));
        Assert.Equal(2, selection.Candidates.Count(candidate => candidate.PredictionCategory == "Over2.5Goals"));
        Assert.Equal(2, selection.Candidates.Count(candidate => candidate.PredictionCategory == "StraightWin"));
    }

    [Fact]
    public void BuildSelection_TreatsTotalCountInMixedPrompt_AsGenericRecommendationRequest()
    {
        var predictions = Enumerable.Range(1, 10)
            .Select(index => CreatePrediction(index, "BothTeamsScore", "BTTS", $"BTTS Home {index}", $"BTTS Away {index}", "Italy - Serie A", 0.88m - (index * 0.005m)))
            .Concat(Enumerable.Range(11, 10)
                .Select(index => CreatePrediction(index, "Over2.5Goals", "Over 2.5", $"Over Home {index}", $"Over Away {index}", "Spain - La Liga", 0.86m - ((index - 10) * 0.005m))))
            .Concat(Enumerable.Range(21, 10)
                .Select(index => CreatePrediction(index, "StraightWin", "Home Win", $"Win Home {index}", $"Win Away {index}", "England - Premier League", 0.89m - ((index - 20) * 0.005m), thresholdUsed: 0.68)))
            .ToArray();

        var selection = AiChatContextBuilder.BuildSelection(
            predictions,
            "Give me a mixture of btts, over 2.5 and straight win. Total 20",
            DateTime.UtcNow);

        Assert.False(selection.NoRelevantMatchesFound);
        Assert.Equal(20, selection.Candidates.Count);
        Assert.Contains(selection.Candidates, candidate => candidate.PredictionCategory == "BothTeamsScore");
        Assert.Contains(selection.Candidates, candidate => candidate.PredictionCategory == "Over2.5Goals");
        Assert.Contains(selection.Candidates, candidate => candidate.PredictionCategory == "StraightWin");
        Assert.Equal(20, selection.RequestedCandidateCount);
    }

    [Fact]
    public void BuildSelection_ReturnsBestEffortSubset_WithShortfallWarning_ForExplicitMarketCounts()
    {
        var predictions = new[]
        {
            CreatePrediction(1, "BothTeamsScore", "BTTS", "Alpha 1", "Beta 1", "Italy - Serie A", 0.82m),
            CreatePrediction(2, "BothTeamsScore", "BTTS", "Alpha 2", "Beta 2", "Italy - Serie A", 0.79m),
            CreatePrediction(3, "Over2.5Goals", "Over 2.5", "Gamma 1", "Delta 1", "Spain - La Liga", 0.81m),
            CreatePrediction(4, "Over2.5Goals", "Over 2.5", "Gamma 2", "Delta 2", "Spain - La Liga", 0.78m)
        };

        var request = new AiChatNormalizedRequest
        {
            Intent = AiChatIntent.RecommendPicks,
            Scope = "today",
            BookableOnly = true,
            RequestedTotalCount = 5,
            RequestedMarkets =
            [
                new AiChatRequestedMarket
                {
                    PredictionCategory = "BothTeamsScore",
                    Count = 5,
                    ExplicitCount = true
                }
            ]
        };

        var selection = AiChatContextBuilder.BuildSelection(
            predictions,
            request,
            DateTime.UtcNow);

        Assert.Equal(2, selection.Candidates.Count);
        Assert.All(selection.Candidates, candidate => Assert.Equal("BothTeamsScore", candidate.PredictionCategory));
        Assert.Contains(selection.ShortfallWarnings, warning => warning.Contains("Requested 5 BTTS", StringComparison.OrdinalIgnoreCase));
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
        Assert.Equal(10, selection.Candidates.Count);
    }

    [Fact]
    public void BuildSelection_SendsFullCard_ForGenericStrongPickRequests()
    {
        var predictions = Enumerable.Range(1, 10)
            .Select(index => CreatePrediction(index, "StraightWin", "Home Win", $"Team {index}", $"Opponent {index}", "England - Premier League", 0.88m - (index * 0.01m), thresholdUsed: 0.68))
            .ToArray();

        var selection = AiChatContextBuilder.BuildSelection(
            predictions,
            "Give me your strong picks for today",
            DateTime.UtcNow);

        Assert.Equal(0, selection.RequestedCandidateCount);
        Assert.Equal(10, selection.Candidates.Count);
    }

    [Fact]
    public void BuildSelection_FiltersToYesterday_WhenPromptMentionsYesterday()
    {
        var nowUtc = DateTime.UtcNow;
        var predictions = new[]
        {
            CreatePrediction(1, "StraightWin", "Home Win", "Arsenal", "Chelsea", "England - Premier League", 0.78m, matchDateTimeUtc: nowUtc.AddHours(2)),
            CreatePrediction(2, "StraightWin", "Home Win", "Arsenal", "Chelsea", "England - Premier League", 0.74m, matchDateTimeUtc: nowUtc.AddDays(-1), date: DateTimeProvider.GetLocalTime().AddDays(-1).ToString("dd-MM-yyyy"), actualScore: "2:1")
        };

        var selection = AiChatContextBuilder.BuildSelection(
            predictions,
            "Tell me about Arsenal yesterday",
            nowUtc);

        var candidate = Assert.Single(selection.Candidates);
        Assert.Equal(2, candidate.PredictionId);
        Assert.Equal("Finished", candidate.MatchState);
    }

    [Fact]
    public void BuildSelection_ReturnsRecentFinishedMatches_ForSettlementQuestions()
    {
        var nowUtc = DateTime.UtcNow;
        var predictions = new[]
        {
            CreatePrediction(1, "StraightWin", "Home Win", "Arsenal", "Chelsea", "England - Premier League", 0.78m, matchDateTimeUtc: nowUtc.AddHours(2)),
            CreatePrediction(2, "BothTeamsScore", "BTTS", "Inter", "Milan", "Italy - Serie A", 0.75m, matchDateTimeUtc: nowUtc.AddDays(-1), date: DateTimeProvider.GetLocalTime().AddDays(-1).ToString("dd-MM-yyyy"), actualScore: "2:2")
        };

        var selection = AiChatContextBuilder.BuildSelection(
            predictions,
            "Why did this settle red?",
            nowUtc);

        var candidate = Assert.Single(selection.Candidates);
        Assert.Equal(2, candidate.PredictionId);
        Assert.Equal("Finished", candidate.MatchState);
    }

    [Fact]
    public void BuildSelection_ReturnsSameFixtureDoubles_WhenBothMarketsArePublished()
    {
        var kickoff = DateTime.UtcNow.AddHours(3);
        var predictions = new[]
        {
            CreatePrediction(1, "BothTeamsScore", "BTTS", "Arsenal", "Chelsea", "England - Premier League", 0.78m, matchDateTimeUtc: kickoff),
            CreatePrediction(2, "Over2.5Goals", "Over 2.5", "Arsenal", "Chelsea", "England - Premier League", 0.74m, matchDateTimeUtc: kickoff),
            CreatePrediction(3, "BothTeamsScore", "BTTS", "Inter", "Milan", "Italy - Serie A", 0.80m, matchDateTimeUtc: kickoff),
            CreatePrediction(4, "Over2.5Goals", "Over 2.5", "Roma", "Lazio", "Italy - Serie A", 0.76m, matchDateTimeUtc: kickoff)
        };

        var selection = AiChatContextBuilder.BuildSelection(
            predictions,
            "Which predictions are marked as both gg and over2.5",
            DateTime.UtcNow);

        Assert.False(selection.NoRelevantMatchesFound);
        Assert.Equal(2, selection.Candidates.Count);
        Assert.Contains(selection.Candidates, candidate => candidate.PredictionId == 1);
        Assert.Contains(selection.Candidates, candidate => candidate.PredictionId == 2);
        Assert.All(selection.Candidates, candidate =>
        {
            Assert.Equal("Arsenal", candidate.HomeTeam);
            Assert.Equal("Chelsea", candidate.AwayTeam);
        });
        Assert.True(selection.NormalizedRequest?.RequireSameFixtureMarkets);
    }

    [Fact]
    public void BuildSelection_ReturnsSameFixtureDoubles_WhenPromptHasPredictionTypo()
    {
        var kickoff = DateTime.UtcNow.AddHours(3);
        var predictions = new[]
        {
            CreatePrediction(1, "BothTeamsScore", "BTTS", "Arsenal", "Chelsea", "England - Premier League", 0.78m, matchDateTimeUtc: kickoff),
            CreatePrediction(2, "Over2.5Goals", "Over 2.5", "Arsenal", "Chelsea", "England - Premier League", 0.74m, matchDateTimeUtc: kickoff),
            CreatePrediction(3, "BothTeamsScore", "BTTS", "Inter", "Milan", "Italy - Serie A", 0.80m, matchDateTimeUtc: kickoff)
        };

        var selection = AiChatContextBuilder.BuildSelection(
            predictions,
            "Which pedictions are listed as both btts and over 2.5",
            DateTime.UtcNow);

        Assert.False(selection.NoRelevantMatchesFound);
        Assert.Equal(2, selection.Candidates.Count);
        Assert.All(selection.Candidates, candidate =>
        {
            Assert.Equal("Arsenal", candidate.HomeTeam);
            Assert.Equal("Chelsea", candidate.AwayTeam);
        });
        Assert.True(selection.NormalizedRequest?.RequireSameFixtureMarkets);
        Assert.Empty(selection.NormalizedRequest?.EntityTerms ?? []);
    }

    [Fact]
    public void BuildSelection_WarnsWhenNoSameFixtureDoublesExist()
    {
        var kickoff = DateTime.UtcNow.AddHours(3);
        var predictions = new[]
        {
            CreatePrediction(1, "BothTeamsScore", "BTTS", "Arsenal", "Chelsea", "England - Premier League", 0.78m, matchDateTimeUtc: kickoff),
            CreatePrediction(2, "Over2.5Goals", "Over 2.5", "Inter", "Milan", "Italy - Serie A", 0.74m, matchDateTimeUtc: kickoff)
        };

        var selection = AiChatContextBuilder.BuildSelection(
            predictions,
            "Which predictions are marked as both gg and over2.5",
            DateTime.UtcNow);

        Assert.Empty(selection.Candidates);
        Assert.True(selection.NoRelevantMatchesFound);
        Assert.Contains(selection.ShortfallWarnings, warning => warning.Contains("BTTS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildSelection_KeepsIndependentMix_WhenExplicitMarketCountsAreRequested()
    {
        var kickoff = DateTime.UtcNow.AddHours(3);
        var predictions = new[]
        {
            CreatePrediction(1, "BothTeamsScore", "BTTS", "Arsenal", "Chelsea", "England - Premier League", 0.78m, matchDateTimeUtc: kickoff),
            CreatePrediction(2, "Over2.5Goals", "Over 2.5", "Inter", "Milan", "Italy - Serie A", 0.74m, matchDateTimeUtc: kickoff),
            CreatePrediction(3, "BothTeamsScore", "BTTS", "Roma", "Lazio", "Italy - Serie A", 0.72m, matchDateTimeUtc: kickoff)
        };

        var selection = AiChatContextBuilder.BuildSelection(
            predictions,
            "Give me 1 gg and 1 over 2.5",
            DateTime.UtcNow);

        Assert.False(selection.NormalizedRequest?.RequireSameFixtureMarkets);
        Assert.Equal(2, selection.Candidates.Count);
        Assert.Contains(selection.Candidates, candidate => candidate.PredictionCategory == "BothTeamsScore");
        Assert.Contains(selection.Candidates, candidate => candidate.PredictionCategory == "Over2.5Goals");
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
        double thresholdUsed = 0.55,
        string? actualScore = null)
    {
        var kickoffUtc = matchDateTimeUtc ?? DateTime.UtcNow.AddHours(2);
        var localKickoff = DateTimeProvider.ConvertUtcToLocal(kickoffUtc);
        var matchLocalDate = string.IsNullOrWhiteSpace(date)
            ? DateOnly.FromDateTime(localKickoff)
            : DateOnly.ParseExact(date, "dd-MM-yyyy", CultureInfo.InvariantCulture);
        var matchLocalTime = TimeOnly.FromDateTime(localKickoff);

        return new Prediction
        {
            Id = id,
            Date = date ?? DateTimeProvider.GetLocalTime().ToString("dd-MM-yyyy"),
            Time = localKickoff.ToString("HH:mm"),
            MatchLocalDate = matchLocalDate,
            MatchLocalTime = matchLocalTime,
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
            WasPublished = true,
            IsCurrentRevision = true,
            ActualScore = actualScore
        };
    }
}
