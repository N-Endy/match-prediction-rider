using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Services;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class AiAppKnowledgeBaseTests
{
    private readonly AiAppKnowledgeBase _knowledgeBase = new();
    private readonly AiChatKnowledgeService _knowledgeService;

    public AiAppKnowledgeBaseTests()
    {
        _knowledgeService = new AiChatKnowledgeService(_knowledgeBase);
    }

    [Theory]
    [InlineData("how do straight win predictions work?", "straight-win")]
    [InlineData("tell me about over 2.5 goals", "over-under-25")]
    [InlineData("what does both teams to score mean?", "btts")]
    [InlineData("explain the draw market", "draws")]
    [InlineData("how does a banker slip work?", "banker-slips")]
    [InlineData("what is weekend payout accumulator?", "weekend-payout")]
    [InlineData("explain value bets and positive edge", "value-bets")]
    [InlineData("what is the brier score metric?", "brier-score")]
    [InlineData("how do sportybet booking codes work?", "booking-codes")]
    [InlineData("explain rollover strategy compounding", "rollover-strategy")]
    public void TryLookup_ResolvesRelevantAppTopics(string query, string expectedTopicId)
    {
        var found = _knowledgeBase.TryLookup(query, out var entry);

        Assert.True(found);
        Assert.NotNull(entry);
        Assert.Equal(expectedTopicId, entry!.TopicId);
        Assert.NotEmpty(entry.Cards);
    }

    [Fact]
    public void TryLookup_ReturnsFalseForIrrelevantQueries()
    {
        var found = _knowledgeBase.TryLookup("what is the weather in Tokyo?", out var entry);

        Assert.False(found);
        Assert.Null(entry);
    }

    [Fact]
    public void AiChatKnowledgeService_ResolvesAppTopicWithKnowledgeCards()
    {
        var found = _knowledgeService.TryBuildPublicAppHelpResponse(
            "What is a banker slip?",
            [],
            out var response,
            out var topic);

        Assert.True(found);
        Assert.Equal("banker-slips", topic);
        Assert.NotNull(response);
        Assert.NotEmpty(response.KnowledgeCards);
        Assert.Contains(response.KnowledgeCards, c => c.Title.Contains("Banker", StringComparison.OrdinalIgnoreCase));
    }
}
