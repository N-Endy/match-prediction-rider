using MatchPredictor.Domain.Models;
using MatchPredictor.Domain.Sourcing;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class HistoricalDatasetSerializerTests
{
    [Fact]
    public void Serialize_IsDeterministicRegardlessOfInputOrder()
    {
        var matchA = BuildScore("EPL", "Arsenal", "Chelsea", "2:1", new DateTime(2026, 1, 2, 15, 0, 0, DateTimeKind.Utc));
        var matchB = BuildScore("EPL", "Liverpool", "Everton", "0:0", new DateTime(2026, 1, 1, 12, 30, 0, DateTimeKind.Utc));
        var matchC = BuildScore("Bundesliga", "Bayern", "Dortmund", "3:3", new DateTime(2026, 1, 1, 12, 30, 0, DateTimeKind.Utc));

        var generatedAt = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);

        var first = HistoricalDatasetSerializer.Serialize([matchA, matchB, matchC], generatedAt);
        var second = HistoricalDatasetSerializer.Serialize([matchC, matchA, matchB], generatedAt);

        Assert.Equal(first, second);
    }

    [Fact]
    public void SerializeThenDeserialize_RoundTripsAllFields()
    {
        var scores = new[]
        {
            BuildScore("EPL", "Arsenal", "Chelsea", "2:1", new DateTime(2026, 1, 2, 15, 0, 0, DateTimeKind.Utc), btts: true),
            BuildScore("EPL", "Liverpool", "Everton", "0:0", new DateTime(2026, 1, 1, 12, 30, 0, DateTimeKind.Utc), isLive: true)
        };

        var json = HistoricalDatasetSerializer.Serialize(scores, DateTime.UtcNow);
        var restored = HistoricalDatasetSerializer.Deserialize(json);

        Assert.Equal(2, restored.Count);
        var arsenal = Assert.Single(restored, score => score.HomeTeam == "Arsenal");
        Assert.Equal("Chelsea", arsenal.AwayTeam);
        Assert.Equal("2:1", arsenal.Score);
        Assert.True(arsenal.BTTSLabel);
        Assert.False(arsenal.IsLive);

        var liverpool = Assert.Single(restored, score => score.HomeTeam == "Liverpool");
        Assert.True(liverpool.IsLive);
    }

    [Fact]
    public void Deserialize_WithUnsupportedSchemaVersion_Throws()
    {
        const string json = "{\"schemaVersion\":999,\"generatedAtUtc\":\"2026-01-01T00:00:00Z\",\"count\":0,\"matches\":[]}";

        Assert.Throws<NotSupportedException>(() => HistoricalDatasetSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_EmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.Empty(HistoricalDatasetSerializer.Deserialize(""));
        Assert.Empty(HistoricalDatasetSerializer.Deserialize("   "));
    }

    private static MatchScore BuildScore(
        string league,
        string home,
        string away,
        string score,
        DateTime matchTimeUtc,
        bool btts = false,
        bool isLive = false)
    {
        return new MatchScore
        {
            League = league,
            HomeTeam = home,
            AwayTeam = away,
            Score = score,
            MatchTime = matchTimeUtc,
            BTTSLabel = btts,
            IsLive = isLive
        };
    }
}
