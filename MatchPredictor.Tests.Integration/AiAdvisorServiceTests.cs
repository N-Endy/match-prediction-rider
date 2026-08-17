using System.Net;
using System.Text;
using System.Text.Json;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Services;
using MatchPredictor.Infrastructure.Services.Llm;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class AiAdvisorServiceTests
{
    [Fact]
    public async Task GetAdviceAsync_DiscardsUnknownActionKeys_AndSuppressesBookAllForSingleValidAction()
    {
        await using var context = CreateContext();
        var predictions = await SeedPredictionsAsync(context, 2);
        var validActionKey = $"P{predictions[0].Id}";

        var handler = new SequenceHttpMessageHandler(
            BuildGroqResponse("""
                {
                  "message": "Here is the safest angle on today's card.",
                  "recommendations": [
                    { "actionKey": "P999999", "explanation": "Ignore this one." },
                    { "actionKey": "P999998", "explanation": "Ignore this too." },
                    { "actionKey": "P1", "explanation": "BTTS clears the line with strong confidence." },
                    { "actionKey": "P1", "explanation": "Duplicate." }
                  ],
                  "showBookAll": true,
                  "warnings": ["Grounded to today's published card."]
                }
                """.Replace("\"P1\"", $"\"{validActionKey}\"")));

        var service = CreateService(context, handler);

        var response = await service.GetAdviceAsync("Give me the best BTTS predictions", "session-1");

        Assert.Equal("Here is the safest angle on today's card.", response.Message);
        var action = Assert.Single(response.Actions);
        Assert.Equal(validActionKey, action.ActionKey);
        Assert.Equal("BTTS clears the line with strong confidence.", action.Explanation);
        Assert.False(response.ShowBookAll);
        Assert.Contains("Grounded to today's published card.", response.Warnings);
        Assert.Contains(response.Warnings, warning => warning.Contains("only 1 are currently available", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetAdviceAsync_ReturnsRawFallbackMessage_WhenModelDoesNotReturnJson()
    {
        await using var context = CreateContext();
        var predictions = await SeedPredictionsAsync(context, 1);

        var handler = new SequenceHttpMessageHandler(
            BuildGroqResponse("This is not valid JSON."));
        var service = CreateService(context, handler);

        var response = await service.GetAdviceAsync("Give me the best BTTS predictions", "session-2");

        Assert.Equal("This is not valid JSON.", response.Message);
        var action = Assert.Single(response.Actions);
        Assert.Equal(predictions[0].Id, action.PredictionId);
        Assert.False(response.ShowBookAll);
    }

    [Fact]
    public async Task GetAdviceAsync_UsesSavedRecommendations_ForBookingFollowUpWithoutCallingAiAgain()
    {
        await using var context = CreateContext();
        var predictions = await SeedPredictionsAsync(context, 2);

        var handler = new SequenceHttpMessageHandler(
            BuildGroqResponse($$"""
                {
                  "message": "These are the two best options on today's card.",
                  "recommendedActionKeys": ["P{{predictions[0].Id}}", "P{{predictions[1].Id}}"],
                  "showBookAll": true
                }
                """));

        var service = CreateService(context, handler);

        var firstResponse = await service.GetAdviceAsync("Give me 2 strong picks", "session-3");
        var followUpResponse = await service.GetAdviceAsync("Book them", "session-3");

        Assert.Equal(2, firstResponse.Actions.Count);
        Assert.Equal(2, followUpResponse.Actions.Count);
        Assert.True(followUpResponse.ShowBookAll);
        Assert.Contains("lined up", followUpResponse.Message);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetAdviceAsync_TrimsSessionHistoryToLastSixTurns()
    {
        await using var context = CreateContext();
        await SeedPredictionsAsync(context, 1);

        var handler = new SequenceHttpMessageHandler(
            Enumerable.Range(1, 7)
                .Select(index => BuildGroqResponse($$"""
                    {
                      "message": "Reply {{index}}",
                      "recommendedActionKeys": [],
                      "showBookAll": false
                    }
                    """))
                .ToArray());

        var cache = new TestDistributedCache();
        var service = CreateService(context, handler, cache);

        for (var index = 1; index <= 7; index++)
        {
            await service.GetAdviceAsync($"Give me pick {index}", "session-4");
        }

        var storedStateJson = cache.GetStoredString("ai-chat-session:session-4");
        Assert.NotNull(storedStateJson);

        var state = JsonSerializer.Deserialize<AiChatSessionState>(storedStateJson!, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(state);
        Assert.Equal(12, state!.History.Count);
        Assert.DoesNotContain(state.History, item => item.Content == "Give me pick 1");
        Assert.DoesNotContain(state.History, item => item.Content == "Reply 1");
        Assert.Contains(state.History, item => item.Content == "Give me pick 7");
        Assert.Contains(state.History, item => item.Content == "Reply 7");
    }

    [Fact]
    public async Task GetAdviceAsync_ReturnsNoPredictionsMessage_WhenOnlyPastMatchesRemain()
    {
        await using var context = CreateContext();

        context.Predictions.Add(new Prediction
        {
            Date = DateTimeProvider.GetLocalTime().ToString("dd-MM-yyyy"),
            Time = DateTimeProvider.GetLocalTime().AddHours(-2).ToString("HH:mm"),
            MatchLocalDate = DateOnly.FromDateTime(DateTimeProvider.GetLocalTime()),
            MatchLocalTime = TimeOnly.FromDateTime(DateTimeProvider.GetLocalTime().AddHours(-2)),
            MatchDateTime = DateTime.UtcNow.AddMinutes(-30),
            League = "England - Premier League",
            HomeTeam = "Arsenal",
            AwayTeam = "Chelsea",
            PredictionCategory = "BothTeamsScore",
            PredictedOutcome = "BTTS",
            ConfidenceScore = 0.71m,
            RawConfidenceScore = 0.70m,
            ThresholdUsed = 0.55,
            ThresholdSource = "Configured",
            CalibratorUsed = "Bucket",
            WasPublished = true,
            IsCurrentRevision = true
        });

        await context.SaveChangesAsync();

        var handler = new SequenceHttpMessageHandler();
        var service = CreateService(context, handler);

        var response = await service.GetAdviceAsync("What are the best BTTS picks?", "session-5");

        Assert.Contains("No predictions are available", response.Message);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetAdviceAsync_ListsSameFixtureDoubles_WithoutCallingLlm()
    {
        await using var context = CreateContext();
        await SeedPredictionsAsync(
            context,
            [
                ("BothTeamsScore", "BTTS", 0.78m, 0.55d, "Arsenal", "Chelsea", "England - Premier League"),
                ("Over2.5Goals", "Over 2.5", 0.74m, 0.58d, "Arsenal", "Chelsea", "England - Premier League"),
                ("BothTeamsScore", "BTTS", 0.80m, 0.55d, "Inter", "Milan", "Italy - Serie A"),
                ("Over2.5Goals", "Over 2.5", 0.76m, 0.58d, "Roma", "Lazio", "Italy - Serie A")
            ]);

        var handler = new SequenceHttpMessageHandler(
            BuildGroqResponse("""
                {
                  "message": "This should not be used.",
                  "recommendedActionKeys": [],
                  "showBookAll": false
                }
                """));
        var service = CreateService(context, handler);

        var response = await service.GetAdviceAsync(
            "Which predictions are marked as both gg and over2.5",
            "session-doubles");

        Assert.Equal("catalog_listing", response.ContextMode);
        Assert.Equal(0, handler.CallCount);
        Assert.Equal(2, response.Actions.Count);
        Assert.Contains(response.Actions, action => action.Market == "BTTS" && action.HomeTeam == "Arsenal");
        Assert.Contains(response.Actions, action => action.Market == "Over2.5" && action.HomeTeam == "Arsenal");
        Assert.Contains("Arsenal", response.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Chelsea", response.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(response.AutoBook);
    }

    [Fact]
    public async Task GetAdviceAsync_AutoBooksSameFixtureDoubles_WhenUserAsksToBook()
    {
        await using var context = CreateContext();
        await SeedPredictionsAsync(
            context,
            [
                ("BothTeamsScore", "BTTS", 0.78m, 0.55d, "Arsenal", "Chelsea", "England - Premier League"),
                ("Over2.5Goals", "Over 2.5", 0.74m, 0.58d, "Arsenal", "Chelsea", "England - Premier League")
            ]);

        var handler = new SequenceHttpMessageHandler(
            BuildGroqResponse("""
                {
                  "message": "This should not be used.",
                  "recommendedActionKeys": [],
                  "showBookAll": false
                }
                """));
        var service = CreateService(context, handler);

        var response = await service.GetAdviceAsync(
            "Book fixtures marked as both gg and over 2.5",
            "session-book-doubles");

        Assert.True(
            response.ContextMode == "catalog_listing",
            $"Expected catalog_listing but got {response.ContextMode}. Message: {response.Message}");
        Assert.Equal(0, handler.CallCount);
        Assert.Equal(2, response.Actions.Count);
        Assert.True(response.ShowBookAll);
        Assert.True(response.AutoBook);
        Assert.Contains("slip", response.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetAdviceAsync_FillsRequestedMarketSlices_AndEnablesBookAll_ForLargeMixedBookingRequest()
    {
        await using var context = CreateContext();
        await SeedPredictionsAsync(
            context,
            Enumerable.Range(1, 25).Select(index => ("BothTeamsScore", "BTTS", 0.86m - (index * 0.004m), 0.55d, $"BTTS Home {index}", $"BTTS Away {index}", "England - Premier League"))
                .Concat(Enumerable.Range(1, 25).Select(index => ("Over2.5Goals", "Over 2.5", 0.84m - (index * 0.004m), 0.58d, $"Over Home {index}", $"Over Away {index}", "Italy - Serie A")))
                .Concat(Enumerable.Range(1, 25).Select(index => ("StraightWin", "Home Win", 0.82m - (index * 0.004m), 0.68d, $"Straight Home {index}", $"Straight Away {index}", "Spain - La Liga"))));

        var handler = new SequenceHttpMessageHandler(
            BuildGroqResponse("""
                {
                  "message": "I've lined up the strongest mix from today's card.",
                  "recommendedActionKeys": ["P999999"],
                  "showBookAll": false
                }
                """));

        var service = CreateService(context, handler);

        var response = await service.GetAdviceAsync(
            "Give me 20 btts, 20 over and 20 straightwin and book them",
            "session-6");

        Assert.Equal("I've lined up the strongest mix from today's card.", response.Message);
        Assert.Equal(60, response.Actions.Count);
        Assert.Equal(20, response.Actions.Count(action => action.Market == "BTTS"));
        Assert.Equal(20, response.Actions.Count(action => action.Market == "Over2.5"));
        Assert.Equal(20, response.Actions.Count(action => action.Market == "1X2"));
        Assert.All(response.Actions, action => Assert.False(string.IsNullOrWhiteSpace(action.Explanation)));
        Assert.True(response.ShowBookAll);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetAdviceAsync_HandlesMixedPromptWithTotalCount_WithoutFallingIntoNoMatchPath()
    {
        await using var context = CreateContext();
        var predictions = await SeedPredictionsAsync(
            context,
            Enumerable.Range(1, 10).Select(index => ("BothTeamsScore", "BTTS", 0.87m - (index * 0.004m), 0.55d, $"BTTS Home {index}", $"BTTS Away {index}", "England - Premier League"))
                .Concat(Enumerable.Range(1, 10).Select(index => ("Over2.5Goals", "Over 2.5", 0.85m - (index * 0.004m), 0.58d, $"Over Home {index}", $"Over Away {index}", "Italy - Serie A")))
                .Concat(Enumerable.Range(1, 10).Select(index => ("StraightWin", "Home Win", 0.84m - (index * 0.004m), 0.68d, $"Straight Home {index}", $"Straight Away {index}", "Spain - La Liga"))));

        var recommendedKeys = predictions
            .Take(20)
            .Select(prediction => $"\"P{prediction.Id}\"");

        var handler = new SequenceHttpMessageHandler(
            BuildGroqResponse($$"""
                {
                  "message": "Here is a 20-leg mixed card from today's published picks.",
                  "recommendedActionKeys": [{{string.Join(", ", recommendedKeys)}}],
                  "showBookAll": true
                }
                """));

        var service = CreateService(context, handler);
        var response = await service.GetAdviceAsync(
            "Give me a mixture of btts, over 2.5 and straight win. Total 20",
            "session-total-mix");

        Assert.Equal("mixed_market_recommendation", response.ContextMode);
        Assert.Equal(20, response.Actions.Count);
        Assert.DoesNotContain("couldn't find a matching team, league, or fixture", response.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData("You are allowed to select only 7 predictions for me. Which games would you select?", 7)]
    [InlineData("Give me 7 winnable predictions", 7)]
    [InlineData("Give me 7 predictions for today", 7)]
    public async Task GetAdviceAsync_SendsTodaysCardToOpenAi_ForConversationalPickPrompts(string prompt, int expectedCount)
    {
        await using var context = CreateContext();
        var predictions = await SeedPredictionsAsync(context, 10);
        var recommendedKeys = predictions
            .Take(expectedCount)
            .Select(prediction => $"\"P{prediction.Id}\"");

        var handler = new SequenceHttpMessageHandler(
            BuildGroqResponse($$"""
                {
                  "message": "Here are the games I would select from today's card.",
                  "recommendedActionKeys": [{{string.Join(", ", recommendedKeys)}}],
                  "showBookAll": true
                }
                """));

        var service = CreateService(context, handler);
        var response = await service.GetAdviceAsync(prompt, "session-conversational-picks");

        Assert.Equal("recommend_picks", response.ContextMode);
        Assert.Equal(expectedCount, response.Actions.Count);
        Assert.DoesNotContain("couldn't find a matching team, league, or fixture", response.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetAdviceAsync_SendsTodaysCardToOpenAi_WhenUserAsksWhichGamesToChoose()
    {
        await using var context = CreateContext();
        var predictions = await SeedPredictionsAsync(context, 8);
        var recommendedKeys = predictions
            .Take(3)
            .Select(prediction => $"\"P{prediction.Id}\"");

        var handler = new SequenceHttpMessageHandler(
            BuildGroqResponse($$"""
                {
                  "message": "These are the strongest games I would select after researching the card.",
                  "recommendedActionKeys": [{{string.Join(", ", recommendedKeys)}}],
                  "showBookAll": true
                }
                """));

        var service = CreateService(context, handler);
        var response = await service.GetAdviceAsync(
            "If you have to choose the best predictions for me, which games would you select?",
            "session-choose-best");

        Assert.Equal("recommend_picks", response.ContextMode);
        Assert.Equal(3, response.Actions.Count);
        Assert.DoesNotContain("couldn't find a matching team, league, or fixture", response.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetAdviceAsync_CanReturnFifteenRecommendations_FromFullTodaysCard()
    {
        await using var context = CreateContext();
        var predictions = await SeedPredictionsAsync(context, 20);
        var recommendedKeys = predictions
            .Take(15)
            .Select(prediction => $"\"P{prediction.Id}\"");

        var handler = new SequenceHttpMessageHandler(
            BuildGroqResponse($$"""
                {
                  "message": "Here are 15 researched picks from today's full card.",
                  "recommendedActionKeys": [{{string.Join(", ", recommendedKeys)}}],
                  "showBookAll": true
                }
                """));

        var service = CreateService(context, handler);
        var response = await service.GetAdviceAsync("Give me 15 recommendations", "session-fifteen");

        Assert.Equal("recommend_picks", response.ContextMode);
        Assert.Equal(15, response.Actions.Count);
        Assert.DoesNotContain("couldn't find a matching team, league, or fixture", response.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payload does not include", response.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetAdviceAsync_ReturnsNoMatch_WhenTellMeAboutNamedTeamIsMissing()
    {
        await using var context = CreateContext();
        await SeedPredictionsAsync(
            context,
            new[]
            {
                ("StraightWin", "Home Win", 0.78m, 0.68d, "Inter", "Milan", "Italy - Serie A")
            });

        var handler = new SequenceHttpMessageHandler();
        var service = CreateService(context, handler);
        var response = await service.GetAdviceAsync("Tell me about Arsenal", "session-arsenal-missing");

        Assert.Equal("match_discussion", response.ContextMode);
        Assert.Contains("couldn't find a matching team, league, or fixture", response.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Arsenal", response.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(response.Actions);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetAdviceAsync_DiscussesNamedFixture_WhenTellMeAboutTeamIsOnTodaysCard()
    {
        await using var context = CreateContext();
        var predictions = await SeedPredictionsAsync(
            context,
            new[]
            {
                ("StraightWin", "Home Win", 0.78m, 0.68d, "Arsenal", "Chelsea", "England - Premier League"),
                ("Over2.5Goals", "Over 2.5", 0.74m, 0.58d, "Inter", "Milan", "Italy - Serie A"),
                ("BothTeamsScore", "BTTS", 0.72m, 0.55d, "Roma", "Lazio", "Italy - Serie A")
            });

        var handler = new SequenceHttpMessageHandler();
        var service = CreateService(context, handler);
        var response = await service.GetAdviceAsync("Tell me about Arsenal", "session-arsenal-present");

        Assert.Equal("match_discussion", response.ContextMode);
        Assert.Contains("Arsenal", response.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("couldn't find a matching team, league, or fixture", response.Message, StringComparison.OrdinalIgnoreCase);
        var action = Assert.Single(response.Actions);
        Assert.Equal(predictions[0].Id, action.PredictionId);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetAdviceAsync_CanAnswerAboutYesterdayFixtureWithoutReturningBookingActions()
    {
        await using var context = CreateContext();
        var yesterdayLocal = DateTimeProvider.GetLocalTime().AddDays(-1);

        context.Predictions.Add(new Prediction
        {
            Date = yesterdayLocal.ToString("dd-MM-yyyy"),
            Time = yesterdayLocal.AddHours(18).ToString("HH:mm"),
            MatchLocalDate = DateOnly.FromDateTime(yesterdayLocal),
            MatchLocalTime = TimeOnly.FromDateTime(yesterdayLocal.AddHours(18)),
            MatchDateTime = DateTimeProvider.ConvertLocalToUtc(yesterdayLocal.AddHours(18)),
            League = "England - Premier League",
            HomeTeam = "Arsenal",
            AwayTeam = "Chelsea",
            PredictionCategory = "StraightWin",
            PredictedOutcome = "Home Win",
            ConfidenceScore = 0.76m,
            RawConfidenceScore = 0.73m,
            ThresholdUsed = 0.68,
            ThresholdSource = "Configured",
            CalibratorUsed = "Bucket",
            WasPublished = true,
            IsCurrentRevision = true,
            ActualScore = "2:1",
            ActualOutcome = "Home Win",
            IsLive = false
        });

        await context.SaveChangesAsync();

        var handler = new SequenceHttpMessageHandler();
        var service = CreateService(context, handler);
        var response = await service.GetAdviceAsync("Tell me about Arsenal yesterday", "session-yesterday");

        Assert.Contains("2:1", response.Message);
        Assert.Empty(response.Actions);
        Assert.Equal("match_discussion", response.ContextMode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetAdviceAsync_UsesLastContext_ForSettlementFollowUp()
    {
        await using var context = CreateContext();
        var yesterdayLocal = DateTimeProvider.GetLocalTime().AddDays(-1);

        context.Predictions.Add(new Prediction
        {
            Date = yesterdayLocal.ToString("dd-MM-yyyy"),
            Time = yesterdayLocal.AddHours(18).ToString("HH:mm"),
            MatchLocalDate = DateOnly.FromDateTime(yesterdayLocal),
            MatchLocalTime = TimeOnly.FromDateTime(yesterdayLocal.AddHours(18)),
            MatchDateTime = DateTimeProvider.ConvertLocalToUtc(yesterdayLocal.AddHours(18)),
            League = "England - Premier League",
            HomeTeam = "Arsenal",
            AwayTeam = "Chelsea",
            PredictionCategory = "StraightWin",
            PredictedOutcome = "Home Win",
            ConfidenceScore = 0.76m,
            RawConfidenceScore = 0.73m,
            ThresholdUsed = 0.68,
            ThresholdSource = "Configured",
            CalibratorUsed = "Bucket",
            WasPublished = true,
            IsCurrentRevision = true,
            ActualScore = "0:1",
            ActualOutcome = "Away Win",
            IsLive = false
        });

        await context.SaveChangesAsync();

        var handler = new SequenceHttpMessageHandler();

        var cache = new TestDistributedCache();
        var service = CreateService(context, handler, cache);

        var first = await service.GetAdviceAsync("Tell me about Arsenal yesterday", "session-context");
        var second = await service.GetAdviceAsync("Why did this settle red?", "session-context");

        Assert.Contains("0:1", first.Message);
        Assert.Contains("settled red", second.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("settlement_explanation", second.ContextMode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetAdviceAsync_MapsPerActionExplanations_FromStructuredRecommendations()
    {
        await using var context = CreateContext();
        var predictions = await SeedPredictionsAsync(context, 1);

        var handler = new SequenceHttpMessageHandler(
            BuildGroqResponse($$"""
                {
                  "message": "Here is the best fit for that fixture.",
                  "recommendations": [
                    {
                      "actionKey": "P{{predictions[0].Id}}",
                      "explanation": "This pick stays on today's card and clears its threshold with room to spare."
                    }
                  ],
                  "showBookAll": false
                }
                """));

        var service = CreateService(context, handler);

        var response = await service.GetAdviceAsync("Give me the best pick for Beta 1 vs Delta 1", "session-7");

        var action = Assert.Single(response.Actions);
        Assert.Equal($"P{predictions[0].Id}", action.ActionKey);
        Assert.Equal("This pick stays on today's card and clears its threshold with room to spare.", action.Explanation);
    }

    [Fact]
    public async Task GetAdviceAsync_AsksForTargetOdds_WhenRolloverPromptOmitsThem()
    {
        await using var context = CreateContext();
        await SeedPredictionsAsync(context, 3);

        var handler = new SequenceHttpMessageHandler();
        var cache = new TestDistributedCache();
        var service = CreateService(context, handler, cache);

        var response = await service.GetAdviceAsync(
            "I'm doing a rollover today. Give me your strong picks for today",
            "rollover-session-1");

        Assert.Contains("What total odds", response.Message);
        Assert.Empty(response.Actions);
        Assert.False(response.ShowBookAll);
        Assert.Equal(0, handler.CallCount);

        var storedStateJson = cache.GetStoredString("ai-chat-session:rollover-session-1");
        Assert.NotNull(storedStateJson);

        var state = JsonSerializer.Deserialize<AiChatSessionState>(storedStateJson!, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(state);
        Assert.True(state!.AwaitingRolloverTargetOdds);
        Assert.Contains("rollover", state.PendingRolloverPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetAdviceAsync_CompletesPendingRollover_WhenUserRepliesWithTargetOdds()
    {
        await using var context = CreateContext();
        var predictions = await SeedPredictionsAsync(
            context,
            new[]
            {
                ("StraightWin", "Home Win", 0.84m, 0.68d, "Palmeiras", "Sporting Cristal", "CONMEBOL - Copa Libertadores U20"),
                ("BothTeamsScore", "BTTS", 0.78m, 0.55d, "Inter", "Milan", "Italy - Serie A"),
                ("Over2.5Goals", "Over 2.5", 0.76m, 0.58d, "Benfica", "Porto", "Portugal - Primeira Liga")
            });

        await SeedMarketDataAsync(
            context,
            (predictions[0], homeWin: 0.73, draw: 0.15, awayWin: 0.12, over25: 0.58, under25: 0.42, bttsYes: 0.56, bttsNo: 0.44),
            (predictions[1], homeWin: 0.38, draw: 0.28, awayWin: 0.34, over25: 0.53, under25: 0.47, bttsYes: 0.68, bttsNo: 0.32),
            (predictions[2], homeWin: 0.43, draw: 0.25, awayWin: 0.32, over25: 0.67, under25: 0.33, bttsYes: 0.61, bttsNo: 0.39));

        var handler = new SequenceHttpMessageHandler();
        var cache = new TestDistributedCache();
        var service = CreateService(context, handler, cache);

        var firstResponse = await service.GetAdviceAsync(
            "I'm doing a rollover today. Give me your strong picks for today",
            "rollover-session-2");
        var secondResponse = await service.GetAdviceAsync("2 odds", "rollover-session-2");

        Assert.Contains("What total odds", firstResponse.Message);
        Assert.NotEmpty(secondResponse.Actions);
        Assert.True(secondResponse.ShowBookAll);
        Assert.Contains("2", secondResponse.Message);
        Assert.Contains(secondResponse.Warnings, warning => warning.Contains("Estimated combined odds", StringComparison.OrdinalIgnoreCase));
        Assert.All(secondResponse.Actions, action => Assert.True(action.EstimatedOdds > 1.0));
        Assert.All(secondResponse.Actions, action => Assert.False(string.IsNullOrWhiteSpace(action.Explanation)));
        Assert.Equal(0, handler.CallCount);

        var storedStateJson = cache.GetStoredString("ai-chat-session:rollover-session-2");
        Assert.NotNull(storedStateJson);

        var state = JsonSerializer.Deserialize<AiChatSessionState>(storedStateJson!, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(state);
        Assert.False(state!.AwaitingRolloverTargetOdds);
        Assert.True(string.IsNullOrWhiteSpace(state.PendingRolloverPrompt));
    }

    [Fact]
    public async Task GetAdviceAsync_RefusesSecuritySensitivePrompts_WithoutCallingAi()
    {
        await using var context = CreateContext();
        var handler = new SequenceHttpMessageHandler();
        var service = CreateService(context, handler);

        var response = await service.GetAdviceAsync("What is the AI Chat password?", "security-session");

        Assert.Equal("security_refusal", response.ContextMode);
        Assert.Contains("can't reveal", response.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(response.KnowledgeCards);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetAdviceAsync_AnswersAnalyticsGlossaryQuestions_Deterministically()
    {
        await using var context = CreateContext();
        var handler = new SequenceHttpMessageHandler();
        var service = CreateService(context, handler);

        var response = await service.GetAdviceAsync("What does reliability mean?", "glossary-session");

        Assert.Equal("app_help", response.ContextMode);
        Assert.Contains("Reliability", response.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(response.KnowledgeCards);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetAdviceAsync_ExplainsExpectedValue_Deterministically()
    {
        await using var context = CreateContext();
        var handler = new SequenceHttpMessageHandler();
        var service = CreateService(context, handler);

        var response = await service.GetAdviceAsync("What is EV?", "ev-glossary-session");

        Assert.Equal("app_help", response.ContextMode);
        Assert.Contains("Expected value", response.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(response.KnowledgeCards);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetAdviceAsync_RoutesHighestEvPrompt_ToValueBetReport()
    {
        await using var context = CreateContext();
        var predictions = await SeedPredictionsAsync(context, 2);
        var handler = new SequenceHttpMessageHandler();
        var report = new ValueBetReportDto
        {
            Bets =
            [
                new ValueBetDto
                {
                    League = predictions[0].League,
                    HomeTeam = predictions[0].HomeTeam,
                    AwayTeam = predictions[0].AwayTeam,
                    KickoffTime = predictions[0].Time,
                    PredictionCategory = predictions[0].PredictionCategory,
                    PredictedOutcome = predictions[0].PredictedOutcome,
                    MathematicalProbability = 0.74,
                    MarketProbability = 0.56,
                    DecimalOdds = 2.10,
                    ImpliedProbability = 0.47619,
                    ExpectedValuePercent = 0.554,
                    Edge = 0.18,
                    ThresholdUsed = 0.55,
                    ThresholdSource = "Configured",
                    CalibratorUsed = "Bucket",
                    PricingSource = "Live source pull",
                    OddsFreshness = "Fresh from today's source pricing pull.",
                    OddsDerivationSource = "Source decimal odds",
                    EdgeSource = "Model 74.0% vs market 56.0%",
                    AiJustification = "The model is materially ahead of the market on this leg."
                }
            ]
        };

        var service = CreateService(context, handler, valueBetsService: new StubValueBetsService(report));
        var response = await service.GetAdviceAsync("Which picks have the highest EV today?", "ev-picks-session");

        Assert.Equal("recommend_picks", response.ContextMode);
        var action = Assert.Single(response.Actions);
        Assert.Equal(predictions[0].Id, action.PredictionId);
        Assert.Contains("EV +55.4%", action.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetAdviceAsync_StoresWorkingSlipAndSummary_AfterRecommendationResponse()
    {
        await using var context = CreateContext();
        var predictions = await SeedPredictionsAsync(context, 3);
        var cache = new TestDistributedCache();
        var handler = new SequenceHttpMessageHandler(
            BuildGroqResponse($$"""
                {
                  "message": "Here are the strongest two picks on today's card.",
                  "recommendedActionKeys": ["P{{predictions[0].Id}}", "P{{predictions[1].Id}}"],
                  "showBookAll": true
                }
                """));

        var service = CreateService(context, handler, cache);
        var response = await service.GetAdviceAsync("Give me 2 strong picks", "working-slip-session");

        Assert.Equal("recommend_picks", response.ContextMode);
        Assert.NotNull(response.WorkingSlipSummary);
        Assert.Equal(2, response.WorkingSlipSummary!.Count);
        Assert.Contains("Which is riskiest?", response.SuggestedPrompts);

        var storedStateJson = cache.GetStoredString("ai-chat-session:working-slip-session");
        var state = JsonSerializer.Deserialize<AiChatSessionState>(storedStateJson!, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(state);
        Assert.Equal(2, state!.WorkingSlipPredictionIds.Count);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetAdviceAsync_RanksRiskiestFromWorkingSlip_WithoutCallingAiAgain()
    {
        await using var context = CreateContext();
        var predictions = await SeedPredictionsAsync(
            context,
            new[]
            {
                ("StraightWin", "Home Win", 0.84m, 0.68d, "Safe Home", "Safe Away", "England - Premier League"),
                ("Draw", "Draw", 0.34m, 0.30d, "Draw Home", "Draw Away", "Italy - Serie A"),
                ("BothTeamsScore", "BTTS", 0.74m, 0.55d, "BTTS Home", "BTTS Away", "Spain - La Liga")
            });

        var cache = new TestDistributedCache();
        var handler = new SequenceHttpMessageHandler(
            BuildGroqResponse($$"""
                {
                  "message": "Here is the mix.",
                  "recommendedActionKeys": ["P{{predictions[0].Id}}", "P{{predictions[1].Id}}", "P{{predictions[2].Id}}"],
                  "showBookAll": true
                }
                """));
        var service = CreateService(context, handler, cache);

        await service.GetAdviceAsync("Give me 3 strong picks", "risk-session");
        var response = await service.GetAdviceAsync("Which is riskiest?", "risk-session");

        Assert.Equal("working_slip_refinement", response.ContextMode);
        Assert.Contains("riskiest leg", response.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, response.Actions.Count);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetAdviceAsync_RemovesWeakestLeg_FromWorkingSlip()
    {
        await using var context = CreateContext();
        var predictions = await SeedPredictionsAsync(
            context,
            new[]
            {
                ("StraightWin", "Home Win", 0.84m, 0.68d, "Safe Home", "Safe Away", "England - Premier League"),
                ("Draw", "Draw", 0.34m, 0.30d, "Draw Home", "Draw Away", "Italy - Serie A"),
                ("BothTeamsScore", "BTTS", 0.74m, 0.55d, "BTTS Home", "BTTS Away", "Spain - La Liga")
            });

        var handler = new SequenceHttpMessageHandler(
            BuildGroqResponse($$"""
                {
                  "message": "Here is the mix.",
                  "recommendedActionKeys": ["P{{predictions[0].Id}}", "P{{predictions[1].Id}}", "P{{predictions[2].Id}}"],
                  "showBookAll": true
                }
                """));
        var service = CreateService(context, handler, new TestDistributedCache());

        await service.GetAdviceAsync("Give me 3 strong picks", "remove-session");
        var response = await service.GetAdviceAsync("Remove the weakest one", "remove-session");

        Assert.Equal(2, response.Actions.Count);
        Assert.DoesNotContain(response.Actions, action => action.Prediction == "Draw");
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetAdviceAsync_SwapsDrawOut_FromWorkingSlip()
    {
        await using var context = CreateContext();
        var predictions = await SeedPredictionsAsync(
            context,
            new[]
            {
                ("Draw", "Draw", 0.34m, 0.30d, "Draw Home", "Draw Away", "Italy - Serie A"),
                ("BothTeamsScore", "BTTS", 0.76m, 0.55d, "BTTS Home", "BTTS Away", "Spain - La Liga"),
                ("StraightWin", "Home Win", 0.83m, 0.68d, "Straight Home", "Straight Away", "England - Premier League"),
                ("Over2.5Goals", "Over 2.5", 0.79m, 0.58d, "Over Home", "Over Away", "Portugal - Primeira Liga")
            });

        var handler = new SequenceHttpMessageHandler(
            BuildGroqResponse($$"""
                {
                  "message": "Here is the mix.",
                  "recommendedActionKeys": ["P{{predictions[0].Id}}", "P{{predictions[1].Id}}", "P{{predictions[2].Id}}"],
                  "showBookAll": true
                }
                """));
        var service = CreateService(context, handler, new TestDistributedCache());

        await service.GetAdviceAsync("Give me a mixture of btts, draw and straight win", "swap-session");
        var response = await service.GetAdviceAsync("Swap one draw out", "swap-session");

        Assert.Equal(3, response.Actions.Count);
        Assert.DoesNotContain(response.Actions, action => action.Prediction == "Draw");
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetAdviceAsync_MakesWorkingSlipSafer_WithoutCallingAiAgain()
    {
        await using var context = CreateContext();
        var predictions = await SeedPredictionsAsync(
            context,
            new[]
            {
                ("Draw", "Draw", 0.34m, 0.30d, "Draw Home", "Draw Away", "Italy - Serie A"),
                ("StraightWin", "Home Win", 0.72m, 0.68d, "Risk Home", "Risk Away", "Spain - La Liga"),
                ("StraightWin", "Home Win", 0.86m, 0.68d, "Safe Home 1", "Safe Away 1", "England - Premier League"),
                ("StraightWin", "Home Win", 0.84m, 0.68d, "Safe Home 2", "Safe Away 2", "Portugal - Primeira Liga"),
                ("BothTeamsScore", "BTTS", 0.78m, 0.55d, "Safe Home 3", "Safe Away 3", "Germany - Bundesliga")
            });

        var handler = new SequenceHttpMessageHandler(
            BuildGroqResponse($$"""
                {
                  "message": "Here is the mix.",
                  "recommendedActionKeys": ["P{{predictions[0].Id}}", "P{{predictions[1].Id}}", "P{{predictions[2].Id}}"],
                  "showBookAll": true
                }
                """));
        var service = CreateService(context, handler, new TestDistributedCache());

        await service.GetAdviceAsync("Give me 3 strong picks", "safer-session");
        var response = await service.GetAdviceAsync("Make it safer", "safer-session");

        Assert.Equal(3, response.Actions.Count);
        Assert.DoesNotContain(response.Actions, action => action.Prediction == "Draw");
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetAdviceAsync_TargetsOdds_FromExistingWorkingSlipFirst()
    {
        await using var context = CreateContext();
        var predictions = await SeedPredictionsAsync(
            context,
            new[]
            {
                ("StraightWin", "Home Win", 0.84m, 0.68d, "Palmeiras", "Sporting Cristal", "CONMEBOL - Copa Libertadores U20"),
                ("BothTeamsScore", "BTTS", 0.78m, 0.55d, "Inter", "Milan", "Italy - Serie A"),
                ("Over2.5Goals", "Over 2.5", 0.76m, 0.58d, "Benfica", "Porto", "Portugal - Primeira Liga")
            });

        await SeedMarketDataAsync(
            context,
            (predictions[0], homeWin: 0.73, draw: 0.15, awayWin: 0.12, over25: 0.58, under25: 0.42, bttsYes: 0.56, bttsNo: 0.44),
            (predictions[1], homeWin: 0.38, draw: 0.28, awayWin: 0.34, over25: 0.53, under25: 0.47, bttsYes: 0.68, bttsNo: 0.32),
            (predictions[2], homeWin: 0.43, draw: 0.25, awayWin: 0.32, over25: 0.67, under25: 0.33, bttsYes: 0.61, bttsNo: 0.39));

        var handler = new SequenceHttpMessageHandler(
            BuildGroqResponse($$"""
                {
                  "message": "Here is the mix.",
                  "recommendedActionKeys": ["P{{predictions[0].Id}}", "P{{predictions[1].Id}}", "P{{predictions[2].Id}}"],
                  "showBookAll": true
                }
                """));
        var service = CreateService(context, handler, new TestDistributedCache());

        await service.GetAdviceAsync("Give me 3 strong picks", "odds-session");
        var response = await service.GetAdviceAsync("Give me 2 odds from these", "odds-session");

        Assert.Equal("working_slip_refinement", response.ContextMode);
        Assert.NotEmpty(response.Actions);
        Assert.Contains("closest grounded build", response.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(response.WorkingSlipSummary);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetAdviceAsync_UsesTwoStepFootballLookup_AndSurfacesAnalysisFields()
    {
        await using var context = CreateContext();
        var predictions = await SeedPredictionsAsync(context, 6);

        var firstActionKey = $"P{predictions[0].Id}";
        var secondActionKey = $"P{predictions[1].Id}";

        var handler = new SequenceHttpMessageHandler(
            BuildGroqResponse($$"""
                {
                  "message": "Here are researched picks from today's card.",
                  "recommendedActionKeys": ["{{firstActionKey}}", "{{secondActionKey}}"],
                  "showBookAll": true
                }
                """));

        var footballInsightService = new StubFootballInsightService(new Dictionary<string, FootballMatchInsightSnapshot>
        {
            [firstActionKey] = CreateInsightSnapshot(predictions[0].HomeTeam, predictions[0].AwayTeam),
            [secondActionKey] = CreateInsightSnapshot(predictions[1].HomeTeam, predictions[1].AwayTeam)
        });

        var service = CreateService(context, handler, footballInsightService: footballInsightService);

        var response = await service.GetAdviceAsync("Give me 5 strong picks", "football-lookup-session");

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(5, response.Actions.Count);
        Assert.Contains(response.Actions, action => action.ActionKey == firstActionKey && !string.IsNullOrWhiteSpace(action.AnalysisSummary));
        Assert.Contains(response.Actions, action => action.ActionKey == secondActionKey && action.InsightBullets.Count > 0);
    }

    private static async Task<List<Prediction>> SeedPredictionsAsync(ApplicationDbContext context, int count)
    {
        var localNow = DateTimeProvider.GetLocalTime();
        var date = localNow.ToString("dd-MM-yyyy");
        var kickoffTime = localNow.AddMinutes(30).ToString("HH:mm");

        var predictions = Enumerable.Range(1, count)
            .Select(index => new Prediction
            {
                Date = date,
                Time = kickoffTime,
                MatchLocalDate = DateOnly.FromDateTime(localNow),
                MatchLocalTime = TimeOnly.FromDateTime(localNow.AddMinutes(30)),
                MatchDateTime = null,
                League = index % 2 == 0 ? "England - Premier League" : "Italy - Serie A",
                HomeTeam = index % 2 == 0 ? $"Alpha {index}" : $"Beta {index}",
                AwayTeam = index % 2 == 0 ? $"Gamma {index}" : $"Delta {index}",
                PredictionCategory = index % 2 == 0 ? "StraightWin" : "BothTeamsScore",
                PredictedOutcome = index % 2 == 0 ? "Home Win" : "BTTS",
                ConfidenceScore = 0.75m - (index * 0.02m),
                RawConfidenceScore = 0.73m - (index * 0.02m),
                ThresholdUsed = index % 2 == 0 ? 0.68 : 0.55,
                ThresholdSource = "Configured",
                CalibratorUsed = "Bucket",
                WasPublished = true,
                IsCurrentRevision = true
            })
            .ToList();

        context.Predictions.AddRange(predictions);
        await context.SaveChangesAsync();
        return predictions;
    }

    private static async Task<List<Prediction>> SeedPredictionsAsync(
        ApplicationDbContext context,
        IEnumerable<(string Category, string Outcome, decimal Confidence, double ThresholdUsed, string HomeTeam, string AwayTeam, string League)> specs)
    {
        var localNow = DateTimeProvider.GetLocalTime();
        var date = localNow.ToString("dd-MM-yyyy");
        var kickoffTime = localNow.AddMinutes(30).ToString("HH:mm");

        var predictions = specs
            .Select((spec, index) => new Prediction
            {
                Date = date,
                Time = kickoffTime,
                MatchLocalDate = DateOnly.FromDateTime(localNow),
                MatchLocalTime = TimeOnly.FromDateTime(localNow.AddMinutes(30)),
                MatchDateTime = null,
                League = spec.League,
                HomeTeam = spec.HomeTeam,
                AwayTeam = spec.AwayTeam,
                PredictionCategory = spec.Category,
                PredictedOutcome = spec.Outcome,
                ConfidenceScore = spec.Confidence,
                RawConfidenceScore = spec.Confidence - 0.01m,
                ThresholdUsed = spec.ThresholdUsed,
                ThresholdSource = "Configured",
                CalibratorUsed = "Bucket",
                WasPublished = true,
                IsCurrentRevision = true
            })
            .ToList();

        context.Predictions.AddRange(predictions);
        await context.SaveChangesAsync();
        return predictions;
    }

    [Fact]
    public async Task SelectBankerPicksAsync_IncludesFootballInsightsInPayload()
    {
        await using var context = CreateContext();
        var handler = new SequenceHttpMessageHandler(
            BuildGroqResponse("""
                {"picks":[{"predictionId":11,"reason":"Form supports BTTS"}],"riskNote":"Check XI."}
                """));
        var insight = CreateInsightSnapshot("Banker Home", "Banker Away");
        var service = CreateService(
            context,
            handler,
            footballInsightService: new StubFootballInsightService(new Dictionary<string, FootballMatchInsightSnapshot>
            {
                ["11"] = insight
            }));

        var result = await service.SelectBankerPicksAsync(
            [
                new BankerPickRequest
                {
                    PredictionId = 11,
                    League = "Test League",
                    HomeTeam = "Banker Home",
                    AwayTeam = "Banker Away",
                    Market = "BTTS",
                    PredictedOutcome = "BTTS",
                    PredictionCategory = "BothTeamsScore",
                    Confidence = 0.82m,
                    DecimalOdds = 1.55,
                    MatchDateTimeUtc = DateTime.UtcNow.AddHours(4)
                },
                new BankerPickRequest
                {
                    PredictionId = 12,
                    League = "Test League",
                    HomeTeam = "Other Home",
                    AwayTeam = "Other Away",
                    Market = "Over2.5",
                    PredictedOutcome = "Over 2.5",
                    PredictionCategory = "Over2.5Goals",
                    Confidence = 0.80m,
                    DecimalOdds = 1.60,
                    MatchDateTimeUtc = DateTime.UtcNow.AddHours(5)
                }
            ],
            minOdds: 1.5,
            maxOdds: 10);

        Assert.Equal(11, Assert.Single(result.Picks).PredictionId);
        Assert.Contains("footballInsight", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.Contains("dataQuality", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.Contains("form", handler.RequestBodies[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SelectBankerPicksAsync_OpenAi_UsesWebSearchAndKeepsSuppliedIdsOnly()
    {
        await using var context = CreateContext();
        var handler = new SequenceHttpMessageHandler(
            BuildResponsesApiResponse("""
                {"picks":[{"predictionId":11,"reason":"No injury news"},{"predictionId":999,"reason":"invented"}],"riskNote":"Verify XI."}
                """));
        var service = CreateService(
            context,
            handler,
            footballInsightService: new StubFootballInsightService(new Dictionary<string, FootballMatchInsightSnapshot>
            {
                ["11"] = CreateInsightSnapshot("Banker Home", "Banker Away")
            }),
            llmConfig: new Dictionary<string, string?>
            {
                ["AiLlm:Provider"] = "openai",
                ["AiLlm:ApiKey"] = "sk-test",
                ["AiLlm:Model"] = "gpt-5.6-luna",
                ["AiLlm:BaseUrl"] = "https://api.openai.com/v1/"
            });

        var result = await service.SelectBankerPicksAsync(
            [
                new BankerPickRequest
                {
                    PredictionId = 11,
                    League = "Test League",
                    HomeTeam = "Banker Home",
                    AwayTeam = "Banker Away",
                    Market = "BTTS",
                    PredictedOutcome = "BTTS",
                    PredictionCategory = "BothTeamsScore",
                    Confidence = 0.82m,
                    DecimalOdds = 1.55,
                    MatchDateTimeUtc = DateTime.UtcNow.AddHours(4)
                }
            ],
            minOdds: 1.5,
            maxOdds: 10);

        Assert.Equal(11, Assert.Single(result.Picks).PredictionId);
        Assert.Equal("Verify XI.", result.RiskNote);
        Assert.Contains("/responses", handler.RequestUris[0], StringComparison.Ordinal);
        Assert.Contains("web_search", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.Contains("footballInsight", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("chat/completions", handler.RequestUris[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectBankerPicksAsync_OpenAi_ReturnsEmpty_OnJunkResponsesContent()
    {
        await using var context = CreateContext();
        var handler = new SequenceHttpMessageHandler(
            BuildResponsesApiResponse("this is not banker json"));
        var service = CreateService(
            context,
            handler,
            llmConfig: new Dictionary<string, string?>
            {
                ["AiLlm:Provider"] = "openai",
                ["AiLlm:ApiKey"] = "sk-test",
                ["AiLlm:BaseUrl"] = "https://api.openai.com/v1/"
            });

        var result = await service.SelectBankerPicksAsync(
            [
                new BankerPickRequest
                {
                    PredictionId = 11,
                    League = "Test League",
                    HomeTeam = "Banker Home",
                    AwayTeam = "Banker Away",
                    Market = "BTTS",
                    PredictedOutcome = "BTTS",
                    PredictionCategory = "BothTeamsScore",
                    Confidence = 0.82m,
                    DecimalOdds = 1.55
                }
            ],
            minOdds: 1.5,
            maxOdds: 10);

        Assert.Empty(result.Picks);
        Assert.Contains("/responses", handler.RequestUris[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectRolloverPickAsync_DropsUnknownIds()
    {
        await using var context = CreateContext();
        var handler = new SequenceHttpMessageHandler(
            BuildGroqResponse("""
                {"picks":[{"predictionId":999,"reason":"invented"}],"riskNote":"Skip."}
                """));
        var service = CreateService(context, handler);

        var result = await service.SelectRolloverPickAsync(
            [
                new BankerPickRequest
                {
                    PredictionId = 11,
                    League = "Test League",
                    HomeTeam = "Roll Home",
                    AwayTeam = "Roll Away",
                    Market = "BTTS",
                    PredictedOutcome = "BTTS",
                    PredictionCategory = "BothTeamsScore",
                    Confidence = 0.81m,
                    DecimalOdds = 1.30
                }
            ],
            minOdds: 1.20,
            maxOdds: 1.50);

        Assert.Empty(result.Picks);
    }

    [Fact]
    public async Task SelectRolloverPickAsync_OpenAi_UsesWebSearchAndKeepsSuppliedIdsOnly()
    {
        await using var context = CreateContext();
        var handler = new SequenceHttpMessageHandler(
            BuildResponsesApiResponse("""
                {"picks":[{"predictionId":11,"reason":"No injury news"},{"predictionId":999,"reason":"invented"}],"riskNote":"Stack carefully."}
                """));
        var service = CreateService(
            context,
            handler,
            footballInsightService: new StubFootballInsightService(new Dictionary<string, FootballMatchInsightSnapshot>
            {
                ["11"] = CreateInsightSnapshot("Roll Home", "Roll Away")
            }),
            llmConfig: new Dictionary<string, string?>
            {
                ["AiLlm:Provider"] = "openai",
                ["AiLlm:ApiKey"] = "sk-test",
                ["AiLlm:Model"] = "gpt-5.6-luna",
                ["AiLlm:BaseUrl"] = "https://api.openai.com/v1/"
            });

        var result = await service.SelectRolloverPickAsync(
            [
                new BankerPickRequest
                {
                    PredictionId = 11,
                    League = "Test League",
                    HomeTeam = "Roll Home",
                    AwayTeam = "Roll Away",
                    Market = "BTTS",
                    PredictedOutcome = "BTTS",
                    PredictionCategory = "BothTeamsScore",
                    Confidence = 0.81m,
                    DecimalOdds = 1.30,
                    MatchDateTimeUtc = DateTime.UtcNow.AddHours(4)
                }
            ],
            minOdds: 1.20,
            maxOdds: 1.50);

        Assert.Equal(11, Assert.Single(result.Picks).PredictionId);
        Assert.Equal("Stack carefully.", result.RiskNote);
        Assert.Contains("/responses", handler.RequestUris[0], StringComparison.Ordinal);
        Assert.Contains("web_search", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.Contains("footballInsight", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("chat/completions", handler.RequestUris[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectBestDrawPicksAsync_IncludesFootballInsightsAndSelectsFromSuppliedIdsOnly()
    {
        await using var context = CreateContext();
        var handler = new SequenceHttpMessageHandler(
            BuildGroqResponse("""
                {"picks":[{"predictionId":22,"reason":"High H2H draw rate"},{"predictionId":999,"reason":"invented"}]}
                """));
        var service = CreateService(
            context,
            handler,
            footballInsightService: new StubFootballInsightService(new Dictionary<string, FootballMatchInsightSnapshot>
            {
                ["22"] = CreateInsightSnapshot("Draw Home", "Draw Away")
            }));

        var selected = await service.SelectBestDrawPicksAsync(
            [
                new BetslipDrawPickRequest
                {
                    PredictionId = 21,
                    League = "Draw League",
                    HomeTeam = "Low Conf Home",
                    AwayTeam = "Low Conf Away",
                    Confidence = 0.90m,
                    MatchDateTimeUtc = DateTime.UtcNow.AddHours(3),
                    PredictionCategory = "Draw"
                },
                new BetslipDrawPickRequest
                {
                    PredictionId = 22,
                    League = "Draw League",
                    HomeTeam = "Draw Home",
                    AwayTeam = "Draw Away",
                    Confidence = 0.55m,
                    MatchDateTimeUtc = DateTime.UtcNow.AddHours(4),
                    PredictionCategory = "Draw"
                }
            ],
            count: 5);

        Assert.Equal(22, Assert.Single(selected).PredictionId);
        Assert.Contains("footballInsight", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain(selected, pick => pick.PredictionId == 999);
    }

    [Fact]
    public async Task SelectBestDrawPicksAsync_OpenAi_UsesWebSearchAndKeepsSuppliedIdsOnly()
    {
        await using var context = CreateContext();
        var handler = new SequenceHttpMessageHandler(
            BuildResponsesApiResponse("""
                {"picks":[{"predictionId":22,"reason":"No late news"},{"predictionId":999,"reason":"invented"}]}
                """));
        var service = CreateService(
            context,
            handler,
            footballInsightService: new StubFootballInsightService(new Dictionary<string, FootballMatchInsightSnapshot>
            {
                ["22"] = CreateInsightSnapshot("Draw Home", "Draw Away")
            }),
            llmConfig: new Dictionary<string, string?>
            {
                ["AiLlm:Provider"] = "openai",
                ["AiLlm:ApiKey"] = "sk-test",
                ["AiLlm:Model"] = "gpt-5.6-luna",
                ["AiLlm:BaseUrl"] = "https://api.openai.com/v1/"
            });

        var selected = await service.SelectBestDrawPicksAsync(
            [
                new BetslipDrawPickRequest
                {
                    PredictionId = 21,
                    League = "Draw League",
                    HomeTeam = "Low Conf Home",
                    AwayTeam = "Low Conf Away",
                    Confidence = 0.90m,
                    MatchDateTimeUtc = DateTime.UtcNow.AddHours(3),
                    PredictionCategory = "Draw"
                },
                new BetslipDrawPickRequest
                {
                    PredictionId = 22,
                    League = "Draw League",
                    HomeTeam = "Draw Home",
                    AwayTeam = "Draw Away",
                    Confidence = 0.55m,
                    MatchDateTimeUtc = DateTime.UtcNow.AddHours(4),
                    PredictionCategory = "Draw"
                }
            ],
            count: 5);

        Assert.Equal(22, Assert.Single(selected).PredictionId);
        Assert.Contains("/responses", handler.RequestUris[0], StringComparison.Ordinal);
        Assert.Contains("web_search", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("chat/completions", handler.RequestUris[0], StringComparison.Ordinal);
        Assert.DoesNotContain(selected, pick => pick.PredictionId == 999);
    }

    [Fact]
    public async Task RankLadderCandidatesAsync_ReturnsSuppliedIdsInModelOrder_DroppingUnknownIds()
    {
        await using var context = CreateContext();
        var handler = new SequenceHttpMessageHandler(
            BuildGroqResponse("""
                {"orderedPredictionIds":[3,1,999,2]}
                """));
        var service = CreateService(
            context,
            handler,
            footballInsightService: new StubFootballInsightService(new Dictionary<string, FootballMatchInsightSnapshot>
            {
                ["3"] = CreateInsightSnapshot("Home 3", "Away 3")
            }));

        var ranked = await service.RankLadderCandidatesAsync(
            [
                new LadderRankRequest
                {
                    PredictionId = 1,
                    League = "Test",
                    HomeTeam = "Home 1",
                    AwayTeam = "Away 1",
                    Market = "BTTS",
                    PredictedOutcome = "BTTS",
                    PredictionCategory = "BothTeamsScore",
                    Confidence = 0.90m,
                    DecimalOdds = 1.55
                },
                new LadderRankRequest
                {
                    PredictionId = 2,
                    League = "Test",
                    HomeTeam = "Home 2",
                    AwayTeam = "Away 2",
                    Market = "Over2.5",
                    PredictedOutcome = "Over 2.5",
                    PredictionCategory = "Over2.5Goals",
                    Confidence = 0.80m,
                    DecimalOdds = 1.60
                },
                new LadderRankRequest
                {
                    PredictionId = 3,
                    League = "Test",
                    HomeTeam = "Home 3",
                    AwayTeam = "Away 3",
                    Market = "Under2.5",
                    PredictedOutcome = "Under 2.5",
                    PredictionCategory = "Under2.5Goals",
                    Confidence = 0.50m,
                    DecimalOdds = 1.70
                }
            ]);

        Assert.Equal([3, 1, 2], ranked.OrderedPredictionIds);
        Assert.Contains("footballInsight", handler.RequestBodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScreenBetslipCandidatesAsync_ReturnsSuppliedIdsOnly_WithoutWebSearch()
    {
        await using var context = CreateContext();
        var handler = new SequenceHttpMessageHandler(
            BuildGroqResponse("""
                {"passed":[{"predictionId":2,"score":91,"reason":"Form supports"},{"predictionId":999,"score":80,"reason":"invented"}]}
                """));
        var service = CreateService(
            context,
            handler,
            footballInsightService: new StubFootballInsightService(new Dictionary<string, FootballMatchInsightSnapshot>
            {
                ["2"] = CreateInsightSnapshot("Home 2", "Away 2")
            }));

        var result = await service.ScreenBetslipCandidatesAsync(
            [
                new BetslipScreenRequest
                {
                    PredictionId = 1,
                    League = "Test",
                    HomeTeam = "Home 1",
                    AwayTeam = "Away 1",
                    Market = "BTTS",
                    PredictedOutcome = "BTTS",
                    PredictionCategory = "BothTeamsScore",
                    Confidence = 0.80m,
                    DecimalOdds = 1.55
                },
                new BetslipScreenRequest
                {
                    PredictionId = 2,
                    League = "Test",
                    HomeTeam = "Home 2",
                    AwayTeam = "Away 2",
                    Market = "Over2.5",
                    PredictedOutcome = "Over 2.5",
                    PredictionCategory = "Over2.5Goals",
                    Confidence = 0.70m,
                    DecimalOdds = 1.70
                }
            ]);

        Assert.Equal(2, Assert.Single(result.Passed).PredictionId);
        Assert.Contains("footballInsight", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("web_search", handler.RequestBodies[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/responses", handler.RequestUris[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ComposeLadderSlipsAsync_DropsUnknownIds()
    {
        await using var context = CreateContext();
        var handler = new SequenceHttpMessageHandler(
            BuildGroqResponse("""
                {"slips":[{"slipNumber":1,"predictionIds":[2,999,1]}]}
                """));
        var service = CreateService(context, handler);

        var result = await service.ComposeLadderSlipsAsync(
            [
                new LadderRankRequest
                {
                    PredictionId = 1,
                    League = "Test",
                    HomeTeam = "Home 1",
                    AwayTeam = "Away 1",
                    Market = "BTTS",
                    PredictedOutcome = "BTTS",
                    PredictionCategory = "BothTeamsScore",
                    Confidence = 0.80m,
                    DecimalOdds = 1.55
                },
                new LadderRankRequest
                {
                    PredictionId = 2,
                    League = "Test",
                    HomeTeam = "Home 2",
                    AwayTeam = "Away 2",
                    Market = "Over2.5",
                    PredictedOutcome = "Over 2.5",
                    PredictionCategory = "Over2.5Goals",
                    Confidence = 0.75m,
                    DecimalOdds = 1.70
                }
            ],
            [
                new LadderComposeBandRequest
                {
                    SlipNumber = 1,
                    Title = "Small Acca A",
                    BandKey = "small",
                    MinOdds = 20,
                    MaxOdds = 120,
                    FallbackMinOdds = 10,
                    FallbackMaxOdds = 150,
                    MaxPicks = 18
                }
            ]);

        var slip = Assert.Single(result.Slips);
        Assert.Equal(1, slip.SlipNumber);
        Assert.Equal([2, 1], slip.PredictionIds);
    }

    [Fact]
    public async Task ComposeLadderSlipsAsync_OpenAi_UsesWebSearchAndDropsUnknownIds()
    {
        await using var context = CreateContext();
        var handler = new SequenceHttpMessageHandler(
            BuildResponsesApiResponse("""
                {"slips":[{"slipNumber":1,"predictionIds":[2,999,1]}]}
                """));
        var service = CreateService(
            context,
            handler,
            llmConfig: new Dictionary<string, string?>
            {
                ["AiLlm:Provider"] = "openai",
                ["AiLlm:ApiKey"] = "sk-test",
                ["AiLlm:Model"] = "gpt-5.6-luna",
                ["AiLlm:BaseUrl"] = "https://api.openai.com/v1/"
            });

        var result = await service.ComposeLadderSlipsAsync(
            [
                new LadderRankRequest
                {
                    PredictionId = 1,
                    League = "Test",
                    HomeTeam = "Home 1",
                    AwayTeam = "Away 1",
                    Market = "BTTS",
                    PredictedOutcome = "BTTS",
                    PredictionCategory = "BothTeamsScore",
                    Confidence = 0.80m,
                    DecimalOdds = 1.55
                },
                new LadderRankRequest
                {
                    PredictionId = 2,
                    League = "Test",
                    HomeTeam = "Home 2",
                    AwayTeam = "Away 2",
                    Market = "Over2.5",
                    PredictedOutcome = "Over 2.5",
                    PredictionCategory = "Over2.5Goals",
                    Confidence = 0.75m,
                    DecimalOdds = 1.70
                }
            ],
            [
                new LadderComposeBandRequest
                {
                    SlipNumber = 1,
                    Title = "Small Acca A",
                    BandKey = "small",
                    MinOdds = 20,
                    MaxOdds = 120,
                    FallbackMinOdds = 10,
                    FallbackMaxOdds = 150,
                    MaxPicks = 18
                }
            ]);

        var slip = Assert.Single(result.Slips);
        Assert.Equal([2, 1], slip.PredictionIds);
        Assert.Contains("/responses", handler.RequestUris[0], StringComparison.Ordinal);
        Assert.Contains("web_search", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("chat/completions", handler.RequestUris[0], StringComparison.Ordinal);
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options);
    }

    private static async Task SeedMarketDataAsync(
        ApplicationDbContext context,
        params (Prediction Prediction, double homeWin, double draw, double awayWin, double over25, double under25, double bttsYes, double bttsNo)[] specs)
    {
        var matchData = specs.Select(spec => new MatchData
        {
            Date = spec.Prediction.Date,
            Time = spec.Prediction.Time,
            MatchLocalDate = spec.Prediction.MatchLocalDate,
            MatchLocalTime = spec.Prediction.MatchLocalTime,
            MatchDateTime = spec.Prediction.MatchDateTime,
            League = spec.Prediction.League,
            HomeTeam = spec.Prediction.HomeTeam,
            AwayTeam = spec.Prediction.AwayTeam,
            HomeWin = spec.homeWin,
            Draw = spec.draw,
            AwayWin = spec.awayWin,
            OverTwoGoals = spec.over25,
            UnderTwoGoals = spec.under25,
            BttsYes = spec.bttsYes,
            BttsNo = spec.bttsNo
        }).ToList();

        context.MatchDatas.AddRange(matchData);
        await context.SaveChangesAsync();
    }

    private static AiAdvisorService CreateService(
        ApplicationDbContext context,
        SequenceHttpMessageHandler handler,
        TestDistributedCache? cache = null,
        IValueBetsService? valueBetsService = null,
        IAiChatFootballInsightService? footballInsightService = null,
        Dictionary<string, string?>? llmConfig = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(llmConfig ?? new Dictionary<string, string?>
            {
                ["AiLlm:Provider"] = "gemini",
                ["AiLlm:ApiKey"] = "test-api-key",
                ["AiLlm:Model"] = "test-model",
                ["AiLlm:BaseUrl"] = "https://example.test/v1beta/openai/"
            })
            .Build();

        var chatClient = new OpenAiCompatibleChatCompletionsClient(
            new StubHttpClientFactory(handler),
            new AiLlmSettingsResolver(configuration),
            NullLogger<OpenAiCompatibleChatCompletionsClient>.Instance);

        return new AiAdvisorService(
            context,
            NullLogger<AiAdvisorService>.Instance,
            chatClient,
            new AiChatSessionStore(cache ?? new TestDistributedCache(), NullLogger<AiChatSessionStore>.Instance),
            new AiChatKnowledgeService(),
            new AiChatRequestParser(new StubSchemaFallbackService(), NullLogger<AiChatRequestParser>.Instance),
            new StubServiceScopeFactory(valueBetsService),
            footballInsightService ?? new StubFootballInsightService());
    }

    private static FootballMatchInsightSnapshot CreateInsightSnapshot(string homeTeam, string awayTeam)
    {
        return new FootballMatchInsightSnapshot
        {
            HomeTeam = homeTeam,
            AwayTeam = awayTeam,
            InsightSource = "InternalHistory",
            DataQuality = "High",
            HomeForm = new TeamFormSnapshot
            {
                TeamName = homeTeam,
                SampleSize = 5,
                VenueSampleSize = 3,
                Wins = 3,
                Draws = 1,
                Losses = 1,
                PointsPerMatch = 2.0,
                VenuePointsPerMatch = 2.33,
                GoalsForPerMatch = 1.8,
                GoalsAgainstPerMatch = 0.8,
                VenueGoalsForPerMatch = 2.0,
                VenueGoalsAgainstPerMatch = 0.67,
                BttsRate = 0.6,
                Over25Rate = 0.6,
                Under25Rate = 0.4,
                CleanSheetRate = 0.4
            },
            AwayForm = new TeamFormSnapshot
            {
                TeamName = awayTeam,
                SampleSize = 5,
                VenueSampleSize = 3,
                Wins = 1,
                Draws = 2,
                Losses = 2,
                PointsPerMatch = 1.0,
                VenuePointsPerMatch = 0.67,
                GoalsForPerMatch = 1.0,
                GoalsAgainstPerMatch = 1.6,
                VenueGoalsForPerMatch = 0.67,
                VenueGoalsAgainstPerMatch = 1.67,
                BttsRate = 0.6,
                Over25Rate = 0.4,
                Under25Rate = 0.6,
                CleanSheetRate = 0.2
            },
            HeadToHead = new HeadToHeadSummary
            {
                SampleSize = 2,
                HomeTeamWins = 1,
                AwayTeamWins = 0,
                Draws = 1,
                BttsRate = 0.5,
                Over25Rate = 0.5
            }
        };
    }

    private static string BuildGroqResponse(string modelContent)
    {
        return JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content = modelContent
                    }
                }
            }
        });
    }

    private static string BuildResponsesApiResponse(string outputText)
    {
        return JsonSerializer.Serialize(new
        {
            output_text = outputText,
            output = new object[]
            {
                new { type = "web_search_call", id = "ws_test" },
                new
                {
                    type = "message",
                    content = new[] { new { type = "output_text", text = outputText } }
                }
            }
        });
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class SequenceHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;

        public SequenceHttpMessageHandler(params string[] responses)
        {
            _responses = new Queue<string>(responses);
        }

        public int CallCount { get; private set; }
        public List<string> RequestBodies { get; } = [];
        public List<string> RequestUris { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            RequestUris.Add(request.RequestUri?.ToString() ?? string.Empty);

            if (request.Content is not null)
            {
                RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("Unexpected HTTP call with no queued response.");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class StubValueBetsService : IValueBetsService
    {
        private readonly ValueBetReportDto _report;

        public StubValueBetsService(ValueBetReportDto report)
        {
            _report = report;
        }

        public Task<IEnumerable<ValueBetDto>> GetTopValueBetsAsync(int limit = 60, CancellationToken ct = default) =>
            Task.FromResult<IEnumerable<ValueBetDto>>(_report.Bets.Take(limit).ToList());

        public Task<ValueBetReportDto> GetValueBetReportAsync(int limit = 60, CancellationToken ct = default)
        {
            var limitedReport = new ValueBetReportDto
            {
                GeneratedAtLocal = _report.GeneratedAtLocal,
                ConsideredCandidateCount = _report.ConsideredCandidateCount,
                IncludedCandidateCount = Math.Min(limit, _report.Bets.Count),
                Bets = _report.Bets.Take(limit).ToList(),
                Warnings = _report.Warnings.ToList(),
                ExclusionBreakdown = _report.ExclusionBreakdown.ToList()
            };

            return Task.FromResult(limitedReport);
        }
    }

    private sealed class StubFootballInsightService : IAiChatFootballInsightService
    {
        private readonly IReadOnlyDictionary<string, FootballMatchInsightSnapshot> _insights;

        public StubFootballInsightService(IReadOnlyDictionary<string, FootballMatchInsightSnapshot>? insights = null)
        {
            _insights = insights ?? new Dictionary<string, FootballMatchInsightSnapshot>();
        }

        public Task<IReadOnlyDictionary<string, FootballMatchInsightSnapshot>> GetInsightsAsync(
            IReadOnlyCollection<AiChatFootballInsightRequest> requests,
            CancellationToken ct = default)
        {
            IReadOnlyDictionary<string, FootballMatchInsightSnapshot> results = requests
                .Where(request => _insights.ContainsKey(request.ActionKey))
                .ToDictionary(request => request.ActionKey, request => _insights[request.ActionKey], StringComparer.OrdinalIgnoreCase);

            return Task.FromResult(results);
        }
    }

    private sealed class StubSchemaFallbackService : IAiChatSchemaFallbackService
    {
        public Task<AiChatNormalizedRequest?> TryParseAsync(
            string userPrompt,
            AiChatNormalizedRequest deterministicRequest,
            CancellationToken ct = default)
        {
            return Task.FromResult<AiChatNormalizedRequest?>(null);
        }
    }

    private sealed class StubServiceScopeFactory : IServiceScopeFactory
    {
        private readonly IValueBetsService? _valueBetsService;

        public StubServiceScopeFactory(IValueBetsService? valueBetsService)
        {
            _valueBetsService = valueBetsService;
        }

        public IServiceScope CreateScope() => new StubServiceScope(_valueBetsService);
    }

    private sealed class StubServiceScope : IServiceScope
    {
        public StubServiceScope(IValueBetsService? valueBetsService)
        {
            ServiceProvider = new StubServiceProvider(valueBetsService);
        }

        public IServiceProvider ServiceProvider { get; }

        public void Dispose()
        {
        }
    }

    private sealed class StubServiceProvider : IServiceProvider
    {
        private readonly IValueBetsService? _valueBetsService;

        public StubServiceProvider(IValueBetsService? valueBetsService)
        {
            _valueBetsService = valueBetsService;
        }

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IValueBetsService))
            {
                return _valueBetsService;
            }

            return null;
        }
    }

    private sealed class TestDistributedCache : IDistributedCache
    {
        private readonly Dictionary<string, byte[]> _entries = new(StringComparer.Ordinal);

        public byte[]? Get(string key) => _entries.TryGetValue(key, out var value) ? value : null;
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) => _entries.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _entries[key] = value;
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }

        public string? GetStoredString(string key)
        {
            return _entries.TryGetValue(key, out var value)
                ? Encoding.UTF8.GetString(value)
                : null;
        }
    }
}
