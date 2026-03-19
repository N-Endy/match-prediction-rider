using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class AiChatRequestParserTests
{
    [Fact]
    public void ParseDeterministic_ParsesExplicitMarketCounts()
    {
        var result = AiChatRequestParser.ParseDeterministic(
            "I need 15 btts, 3 over 2.5 and 7 straight wins",
            null,
            hasWorkingSlip: false,
            hasContextCandidates: false);

        Assert.Equal(AiChatIntent.MixedMarketRecommendation, result.Request.Intent);
        Assert.Equal("today", result.Request.Scope);
        Assert.True(result.Request.BookableOnly);
        Assert.Equal(25, result.Request.RequestedTotalCount);
        Assert.Collection(
            result.Request.RequestedMarkets.OrderBy(market => market.DisplayName),
            market =>
            {
                Assert.Equal("BTTS", market.DisplayName);
                Assert.Equal(15, market.Count);
                Assert.True(market.ExplicitCount);
            },
            market =>
            {
                Assert.Equal("Over 2.5", market.DisplayName);
                Assert.Equal(3, market.Count);
                Assert.True(market.ExplicitCount);
            },
            market =>
            {
                Assert.Equal("Straight Win", market.DisplayName);
                Assert.Equal(7, market.Count);
                Assert.True(market.ExplicitCount);
            });
        Assert.Contains(result.Request.InterpretationNotes, note => note.Contains("15 BTTS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParseDeterministic_ParsesMixedPromptTotalAcrossNamedMarkets()
    {
        var result = AiChatRequestParser.ParseDeterministic(
            "Give me a mixture of btts, over 2.5 and straight win. Total 20",
            null,
            hasWorkingSlip: false,
            hasContextCandidates: false);

        Assert.Equal(AiChatIntent.MixedMarketRecommendation, result.Request.Intent);
        Assert.Equal(20, result.Request.RequestedTotalCount);
        Assert.True(result.Request.FlexibleMix);
        Assert.Equal(3, result.Request.RequestedMarkets.Count);
        Assert.Contains(result.Request.RequestedMarkets, market => market.PredictionCategory == "BothTeamsScore");
        Assert.Contains(result.Request.RequestedMarkets, market => market.PredictionCategory == "Over2.5Goals");
        Assert.Contains(result.Request.RequestedMarkets, market => market.PredictionCategory == "StraightWin");
        Assert.Contains(result.Request.InterpretationNotes, note => note.Contains("20-pick mix", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParseDeterministic_MapsQuantityWordsToCounts()
    {
        var result = AiChatRequestParser.ParseDeterministic(
            "Give me a couple of draws and a few btts",
            null,
            hasWorkingSlip: false,
            hasContextCandidates: false);

        Assert.Equal(AiChatIntent.MixedMarketRecommendation, result.Request.Intent);
        Assert.Equal(5, result.Request.RequestedTotalCount);
        Assert.Contains(result.Request.RequestedMarkets, market =>
            market.PredictionCategory == "Draw" &&
            market.Count == 2 &&
            market.ExplicitCount);
        Assert.Contains(result.Request.RequestedMarkets, market =>
            market.PredictionCategory == "BothTeamsScore" &&
            market.Count == 3 &&
            market.ExplicitCount);
        Assert.Contains(result.Request.InterpretationNotes, note => note.Contains("couple", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Request.InterpretationNotes, note => note.Contains("few", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParseDeterministic_ParsesUnder25AndGenericWins()
    {
        var result = AiChatRequestParser.ParseDeterministic(
            "Give me 2 under 2.5 and 3 wins",
            null,
            hasWorkingSlip: false,
            hasContextCandidates: false);

        Assert.Equal(AiChatIntent.MixedMarketRecommendation, result.Request.Intent);
        Assert.Equal(5, result.Request.RequestedTotalCount);
        Assert.Contains(result.Request.RequestedMarkets, market =>
            market.PredictionCategory == "Under2.5Goals" &&
            market.Count == 2 &&
            market.ExplicitCount);
        Assert.Contains(result.Request.RequestedMarkets, market =>
            market.PredictionCategory == "StraightWin" &&
            market.Count == 3 &&
            market.ExplicitCount);
    }

    [Fact]
    public void ParseDeterministic_ParsesGenericMixedPrompt_WithOverUnderGgAndWin()
    {
        var result = AiChatRequestParser.ParseDeterministic(
            "Give me a mixture of over, under, gg, and win. Total 8",
            null,
            hasWorkingSlip: false,
            hasContextCandidates: false);

        Assert.Equal(AiChatIntent.MixedMarketRecommendation, result.Request.Intent);
        Assert.Equal(8, result.Request.RequestedTotalCount);
        Assert.True(result.Request.FlexibleMix);
        Assert.Equal(4, result.Request.RequestedMarkets.Count);
        Assert.Contains(result.Request.RequestedMarkets, market => market.PredictionCategory == "Over2.5Goals");
        Assert.Contains(result.Request.RequestedMarkets, market => market.PredictionCategory == "Under2.5Goals");
        Assert.Contains(result.Request.RequestedMarkets, market => market.PredictionCategory == "BothTeamsScore");
        Assert.Contains(result.Request.RequestedMarkets, market => market.PredictionCategory == "StraightWin");
    }

    [Fact]
    public void ParseDeterministic_ParsesGenericPredictionCountRequests()
    {
        var result = AiChatRequestParser.ParseDeterministic(
            "Give me 7 predictions for today",
            null,
            hasWorkingSlip: false,
            hasContextCandidates: false);

        Assert.Equal(AiChatIntent.RecommendPicks, result.Request.Intent);
        Assert.Equal("today", result.Request.Scope);
        Assert.Equal(7, result.Request.RequestedTotalCount);
        Assert.True(result.Request.BookableOnly);
    }

    [Fact]
    public void ParseDeterministic_RecognizesGgAndGoalGoalAsBtts()
    {
        var result = AiChatRequestParser.ParseDeterministic(
            "I need 4 gg and 2 goalgoal",
            null,
            hasWorkingSlip: false,
            hasContextCandidates: false);

        Assert.Equal(AiChatIntent.RecommendPicks, result.Request.Intent);
        var requestedMarket = Assert.Single(result.Request.RequestedMarkets);
        Assert.Equal("BothTeamsScore", requestedMarket.PredictionCategory);
        Assert.Equal(6, requestedMarket.Count);
        Assert.True(requestedMarket.ExplicitCount);
    }

    [Fact]
    public void ParseDeterministic_ParsesStrongStraightWinsForToday()
    {
        var result = AiChatRequestParser.ParseDeterministic(
            "Mix me some strong straight wins for today",
            null,
            hasWorkingSlip: false,
            hasContextCandidates: false);

        Assert.Equal(AiChatIntent.RecommendPicks, result.Request.Intent);
        Assert.Equal("today", result.Request.Scope);
        Assert.True(result.Request.BookableOnly);
        Assert.Equal("strong", result.Request.SafetyBias);
        var requestedMarket = Assert.Single(result.Request.RequestedMarkets);
        Assert.Equal("StraightWin", requestedMarket.PredictionCategory);
        Assert.Null(requestedMarket.Count);
    }

    [Fact]
    public void ParseDeterministic_ParsesBookingRequestForStrongPicks()
    {
        var result = AiChatRequestParser.ParseDeterministic(
            "Book 5 strong picks",
            null,
            hasWorkingSlip: false,
            hasContextCandidates: false);

        Assert.Equal(AiChatIntent.RecommendPicks, result.Request.Intent);
        Assert.Equal(5, result.Request.RequestedTotalCount);
        Assert.True(result.Request.WantsBooking);
        Assert.Equal("book", result.Request.ActionDirective);
        Assert.Equal("strong", result.Request.SafetyBias);
    }

    [Fact]
    public void ParseDeterministic_ParsesTargetOddsFromWorkingSlipFollowUp()
    {
        var result = AiChatRequestParser.ParseDeterministic(
            "Give me 2 odds from these",
            new AiChatSessionState { LastIntent = "recommend_picks" },
            hasWorkingSlip: true,
            hasContextCandidates: true);

        Assert.Equal(AiChatIntent.WorkingSlipRefinement, result.Request.Intent);
        Assert.Equal(2d, result.Request.TargetCombinedOdds);
        Assert.Equal("target_odds", result.Request.ActionDirective);
        Assert.Equal("recommend_picks", result.Request.ReferencedContextMode);
    }

    [Fact]
    public async Task ParseAsync_UsesSemanticFallback_WhenDeterministicParsingNeedsHelp()
    {
        var parser = new AiChatRequestParser(
            new StubSchemaFallbackService(new AiChatNormalizedRequest
            {
                Intent = AiChatIntent.MixedMarketRecommendation,
                Scope = "today",
                BookableOnly = true,
                FlexibleMix = true,
                RequestedTotalCount = 6,
                RequestedMarkets =
                [
                    new AiChatRequestedMarket { PredictionCategory = "StraightWin" },
                    new AiChatRequestedMarket { PredictionCategory = "BothTeamsScore" }
                ],
                InterpretationNotes = ["Semantic fallback interpreted 'bankers' as stronger straight-win leaning picks."]
            }),
            NullLogger<AiChatRequestParser>.Instance);

        var result = await parser.ParseAsync(
            "Sort me a coupon with bankers and btts for today",
            null,
            hasWorkingSlip: false,
            hasContextCandidates: false);

        Assert.True(result.UsedSemanticFallback);
        Assert.True(result.Request.UsedSemanticFallback);
        Assert.False(result.Request.NeedsSemanticFallback);
        Assert.Equal(AiChatIntent.MixedMarketRecommendation, result.Request.Intent);
        Assert.Equal(6, result.Request.RequestedTotalCount);
        Assert.Contains(result.Request.RequestedMarkets, market => market.PredictionCategory == "StraightWin");
        Assert.Contains(result.Request.RequestedMarkets, market => market.PredictionCategory == "BothTeamsScore");
    }

    private sealed class StubSchemaFallbackService : IAiChatSchemaFallbackService
    {
        private readonly AiChatNormalizedRequest? _response;

        public StubSchemaFallbackService(AiChatNormalizedRequest? response)
        {
            _response = response;
        }

        public Task<AiChatNormalizedRequest?> TryParseAsync(
            string userPrompt,
            AiChatNormalizedRequest deterministicRequest,
            CancellationToken ct = default)
        {
            return Task.FromResult(_response);
        }
    }
}
