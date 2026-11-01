using System.Text.Json;
using MatchPredictor.Application.Services;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Services.Llm;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class AutonomousTipsterAgentTests
{
    private sealed class StubChatClient : IChatCompletionsClient
    {
        public bool IsConfigured { get; set; } = true;
        public string Provider => "test";
        public string Model => "test-model";
        public ChatCompletionsResult ResultToReturn { get; set; } = new() { Success = true, Content = "{}" };

        public Task<ChatCompletionsResult> CompleteAsync(ChatCompletionsRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(ResultToReturn);
        }

        public async IAsyncEnumerable<string> StreamAsync(ChatCompletionsRequest request, CancellationToken ct = default)
        {
            yield return ResultToReturn.Content;
        }
    }

    private sealed class StubBookingService : ISportyBetBookingService
    {
        public Task<BookingResult> BookGamesAsync(List<BookingSelection> selections)
        {
            return Task.FromResult(new BookingResult
            {
                Success = true,
                BookingCode = "AUTOBK1",
                BookingUrl = "https://www.sportybet.com/ng/?shareCode=AUTOBK1",
                BookedCount = selections.Count
            });
        }
    }

    [Fact]
    public async Task ReasonAndComposeDossierAsync_WhenLlmNotConfigured_ReturnsNull()
    {
        var chatClient = new StubChatClient { IsConfigured = false };
        var bookingService = new StubBookingService();
        var agent = new AutonomousTipsterAgentService(chatClient, bookingService, NullLogger<AutonomousTipsterAgentService>.Instance);

        var result = await agent.ReasonAndComposeDossierAsync(
            DateOnly.FromDateTime(DateTime.UtcNow),
            [],
            new Dictionary<int, decimal>(),
            0);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReasonAndComposeDossierAsync_WhenLlmFailsOrTimesOut_ReturnsNull()
    {
        var chatClient = new StubChatClient
        {
            IsConfigured = true,
            ResultToReturn = new ChatCompletionsResult
            {
                Success = false,
                IsTimeout = true,
                ErrorBody = "Request timed out after 14s"
            }
        };
        var bookingService = new StubBookingService();
        var agent = new AutonomousTipsterAgentService(chatClient, bookingService, NullLogger<AutonomousTipsterAgentService>.Instance);

        var predictions = new List<Prediction>
        {
            new() { Id = 1, HomeTeam = "Arsenal", AwayTeam = "Chelsea", MatchLocalDate = DateOnly.FromDateTime(DateTime.UtcNow), Date = "2026-11-01", Time = "15:00", WasPublished = true, IsCurrentRevision = true }
        };

        var result = await agent.ReasonAndComposeDossierAsync(
            DateOnly.FromDateTime(DateTime.UtcNow),
            predictions,
            new Dictionary<int, decimal> { [1] = 1.85m },
            1);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReasonAndComposeDossierAsync_ValidJsonResponse_ParsesAdaptiveTicketsAndClampsRisk()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var predictions = new List<Prediction>
        {
            new() { Id = 1, FixtureKey = "fx-1", HomeTeam = "Arsenal", AwayTeam = "Chelsea", MatchLocalDate = today, Date = today.ToString("yyyy-MM-dd"), Time = "15:00", ConfidenceScore = 0.72m, PredictionCategory = "StraightWin", PredictedOutcome = "Arsenal Win" },
            new() { Id = 2, FixtureKey = "fx-2", HomeTeam = "Liverpool", AwayTeam = "Everton", MatchLocalDate = today, Date = today.ToString("yyyy-MM-dd"), Time = "17:30", ConfidenceScore = 0.68m, PredictionCategory = "Over2.5Goals", PredictedOutcome = "Over 2.5" }
        };

        var oddsLookup = new Dictionary<int, decimal>
        {
            [1] = 1.70m,
            [2] = 1.60m
        };

        var llmResponse = new AutonomousSlateLlmResponse
        {
            SlateGrade = "GRADE A — PRIME BOARD (+EV DENSE)",
            ExecutiveBriefing = "High quality slate with dominant home volume and proven xG trends.",
            TacticalWatchouts = "Be cautious with midweek European rotation candidates.",
            RecommendedTotalDailyRiskPercent = 9.5m, // Deliberately exceeding 7.5% ceiling to test guardrail clamping
            Tickets =
            [
                new AutonomousTicketLlmDto
                {
                    PillarKind = "Compounder",
                    Title = "The Capital Compounder",
                    Subtitle = "Pillar 1 · Steady Growth",
                    TargetOddsRange = "1.50x – 2.25x",
                    AllocationPercent = 60m,
                    RecommendedUnits = 3.0m,
                    TacticalAnalysis = "Elite home momentum and tactical superiority in both fixtures.",
                    SelectedPredictionIds = [1, 2],
                    CustomLegRationales = new Dictionary<int, string>
                    {
                        [1] = "Arsenal's counter-press disrupts Chelsea's build-up play.",
                        [2] = "Merseyside derbies at Anfield produce relentless tempo and transition overloads."
                    }
                }
            ]
        };

        var chatClient = new StubChatClient
        {
            IsConfigured = true,
            ResultToReturn = new ChatCompletionsResult
            {
                Success = true,
                Content = JsonSerializer.Serialize(llmResponse)
            }
        };

        var bookingService = new StubBookingService();
        var agent = new AutonomousTipsterAgentService(chatClient, bookingService, NullLogger<AutonomousTipsterAgentService>.Instance);

        var dossier = await agent.ReasonAndComposeDossierAsync(today, predictions, oddsLookup, 10);

        Assert.NotNull(dossier);
        Assert.Equal("Autonomous Reasoning Agent", dossier.EngineMode);
        Assert.Equal("GRADE A — PRIME BOARD (+EV DENSE)", dossier.SlateGrade);
        Assert.Equal(7.5m, dossier.TotalDailyRiskBudgetPercent); // Clamped from 9.5m to 7.5m max cap

        Assert.Single(dossier.Tickets);
        var ticket = dossier.Tickets[0];
        Assert.Equal(OperationsPillarKind.Compounder, ticket.PillarKind);
        Assert.Equal(2, ticket.Legs.Count);
        Assert.Equal(2.72m, ticket.CombinedOdds); // 1.70 * 1.60 = 2.72
        Assert.Equal("AUTOBK1", ticket.BookingCode);
        Assert.Equal("Arsenal's counter-press disrupts Chelsea's build-up play.", ticket.Legs[0].TacticalRationale);
    }

    [Fact]
    public async Task ReasonAndComposeDossierAsync_RejectsDuplicateFixtures_AndPreservesUnmetTickets()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var predictions = new List<Prediction>
        {
            new() { Id = 10, FixtureKey = "fx-derby", HomeTeam = "Inter", AwayTeam = "Milan", MatchLocalDate = today, Date = today.ToString("yyyy-MM-dd"), Time = "20:45", ConfidenceScore = 0.65m, PredictionCategory = "StraightWin", PredictedOutcome = "Inter Win" },
            new() { Id = 11, FixtureKey = "fx-derby", HomeTeam = "Inter", AwayTeam = "Milan", MatchLocalDate = today, Date = today.ToString("yyyy-MM-dd"), Time = "20:45", ConfidenceScore = 0.60m, PredictionCategory = "BothTeamsScore", PredictedOutcome = "BTTS" }
        };

        var oddsLookup = new Dictionary<int, decimal>
        {
            [10] = 2.05m,
            [11] = 1.75m
        };

        var llmResponse = new AutonomousSlateLlmResponse
        {
            SlateGrade = "GRADE B — SOLID DISCIPLINED SLATE",
            ExecutiveBriefing = "Selective board.",
            TacticalWatchouts = "None.",
            RecommendedTotalDailyRiskPercent = 5.0m,
            Tickets =
            [
                new AutonomousTicketLlmDto
                {
                    PillarKind = "ValueEdge",
                    Title = "The Value Edge",
                    AllocationPercent = 30m,
                    RecommendedUnits = 1.5m,
                    SelectedPredictionIds = [10, 11] // Duplicate fixture keys!
                },
                new AutonomousTicketLlmDto
                {
                    PillarKind = "MultiplierSniper",
                    Title = "The Multiplier Sniper",
                    IsPreserved = true,
                    PreservationReason = "Insufficient independent high-odds positive EV lines.",
                    SelectedPredictionIds = []
                }
            ]
        };

        var chatClient = new StubChatClient
        {
            IsConfigured = true,
            ResultToReturn = new ChatCompletionsResult
            {
                Success = true,
                Content = JsonSerializer.Serialize(llmResponse)
            }
        };

        var bookingService = new StubBookingService();
        var agent = new AutonomousTipsterAgentService(chatClient, bookingService, NullLogger<AutonomousTipsterAgentService>.Instance);

        var dossier = await agent.ReasonAndComposeDossierAsync(today, predictions, oddsLookup, 5);

        Assert.NotNull(dossier);
        Assert.Equal(2, dossier.Tickets.Count);

        // Ticket 1: Duplicated fixture key fx-derby should be deduplicated to 1 leg
        var valueTicket = dossier.Tickets.First(t => t.PillarKind == OperationsPillarKind.ValueEdge);
        Assert.Single(valueTicket.Legs);
        Assert.Equal(10, valueTicket.Legs[0].PredictionId);

        // Ticket 2: Preserved capital
        var preservedTicket = dossier.Tickets.First(t => t.PillarKind == OperationsPillarKind.MultiplierSniper);
        Assert.True(preservedTicket.IsPreserved);
        Assert.Equal(0m, preservedTicket.RecommendedUnits);
    }
}
