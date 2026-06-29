using MatchPredictor.Domain.Sourcing;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class ScoreConsensusResolverTests
{
    [Fact]
    public void Resolve_WithNoCandidates_ReturnsEmpty()
    {
        var result = ScoreConsensusResolver.Resolve([]);

        Assert.Same(ScoreConsensusResult.Empty, result);
        Assert.Null(result.Score);
        Assert.False(result.HasConsensus);
    }

    [Fact]
    public void Resolve_WithSingleSource_ReportsConsensus()
    {
        var result = ScoreConsensusResolver.Resolve(
        [
            new ScoreCandidate("FlashScore", "2:1", IsFinished: true, Reliability: 0.6, DateTime.UtcNow)
        ]);

        Assert.Equal("2:1", result.Score);
        Assert.True(result.IsFinished);
        Assert.Equal("FlashScore", result.WinningSource);
        Assert.Equal(1, result.AgreeingSources);
        Assert.Equal(1, result.TotalSources);
        Assert.True(result.HasConsensus);
    }

    [Fact]
    public void Resolve_WhenMajorityAgree_PicksAgreedScore()
    {
        var result = ScoreConsensusResolver.Resolve(
        [
            new ScoreCandidate("FlashScore", "1:1", IsFinished: true, Reliability: 0.5, DateTime.UtcNow),
            new ScoreCandidate("AiScore", "1:1", IsFinished: true, Reliability: 0.5, DateTime.UtcNow),
            new ScoreCandidate("SofaScore", "2:1", IsFinished: true, Reliability: 0.5, DateTime.UtcNow)
        ]);

        Assert.Equal("1:1", result.Score);
        Assert.Equal(2, result.AgreeingSources);
        Assert.Equal(3, result.TotalSources);
        Assert.True(result.HasConsensus);
        Assert.InRange(result.AgreementRatio, 0.66, 0.67);
    }

    [Fact]
    public void Resolve_PrefersFinishedOverHigherWeightedLive()
    {
        var result = ScoreConsensusResolver.Resolve(
        [
            new ScoreCandidate("FlashScore", "0:0", IsFinished: false, Reliability: 1.0, DateTime.UtcNow),
            new ScoreCandidate("AiScore", "3:2", IsFinished: true, Reliability: 0.4, DateTime.UtcNow)
        ]);

        Assert.Equal("3:2", result.Score);
        Assert.True(result.IsFinished);
        Assert.Equal("AiScore", result.WinningSource);
    }

    [Fact]
    public void Resolve_WhenTwoSourcesDisagree_HasNoConsensusAtUnanimousThreshold()
    {
        var result = ScoreConsensusResolver.Resolve(
        [
            new ScoreCandidate("FlashScore", "1:0", IsFinished: true, Reliability: 0.5, DateTime.UtcNow),
            new ScoreCandidate("AiScore", "2:0", IsFinished: true, Reliability: 0.5, DateTime.UtcNow)
        ],
        consensusThreshold: 1.0);

        Assert.False(result.HasConsensus);
        Assert.Equal(2, result.TotalSources);
        Assert.Equal(1, result.AgreeingSources);
    }

    [Fact]
    public void Resolve_IgnoresBlankScores()
    {
        var result = ScoreConsensusResolver.Resolve(
        [
            new ScoreCandidate("FlashScore", "", IsFinished: true, Reliability: 0.9, DateTime.UtcNow),
            new ScoreCandidate("AiScore", "  ", IsFinished: true, Reliability: 0.9, DateTime.UtcNow),
            new ScoreCandidate("SofaScore", "2:2", IsFinished: true, Reliability: 0.3, DateTime.UtcNow)
        ]);

        Assert.Equal("2:2", result.Score);
        Assert.Equal(1, result.TotalSources);
        Assert.True(result.HasConsensus);
    }
}
