using System.Text.Json;
using System.Text.Json.Serialization;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Sourcing;

public sealed record HistoricalMatchRecord
{
    public string League { get; init; } = string.Empty;
    public string HomeTeam { get; init; } = string.Empty;
    public string AwayTeam { get; init; } = string.Empty;
    public string Score { get; init; } = string.Empty;
    public DateTime MatchTimeUtc { get; init; }
    public bool BttsLabel { get; init; }
    public bool IsLive { get; init; }
}

public sealed record HistoricalDatasetEnvelope
{
    public int SchemaVersion { get; init; } = HistoricalDatasetSerializer.CurrentSchemaVersion;
    public DateTime GeneratedAtUtc { get; init; }
    public int Count { get; init; }
    public IReadOnlyList<HistoricalMatchRecord> Matches { get; init; } = [];
}

/// <summary>
/// Pure, deterministic (de)serialization of historical match-score data so that backtests are
/// reproducible: the same set of matches always produces byte-identical JSON regardless of the
/// order rows came out of the database, enabling a versioned dataset to be committed and diffed.
/// </summary>
public static class HistoricalDatasetSerializer
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static string Serialize(IEnumerable<MatchScore> scores, DateTime generatedAtUtc)
    {
        var ordered = scores
            .Select(score => new HistoricalMatchRecord
            {
                League = score.League,
                HomeTeam = score.HomeTeam,
                AwayTeam = score.AwayTeam,
                Score = score.Score,
                MatchTimeUtc = score.MatchTime,
                BttsLabel = score.BTTSLabel,
                IsLive = score.IsLive
            })
            .OrderBy(record => record.MatchTimeUtc)
            .ThenBy(record => record.League, StringComparer.Ordinal)
            .ThenBy(record => record.HomeTeam, StringComparer.Ordinal)
            .ThenBy(record => record.AwayTeam, StringComparer.Ordinal)
            .ThenBy(record => record.Score, StringComparer.Ordinal)
            .ToList();

        var envelope = new HistoricalDatasetEnvelope
        {
            SchemaVersion = CurrentSchemaVersion,
            GeneratedAtUtc = generatedAtUtc,
            Count = ordered.Count,
            Matches = ordered
        };

        return JsonSerializer.Serialize(envelope, SerializerOptions);
    }

    public static IReadOnlyList<MatchScore> Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        var envelope = JsonSerializer.Deserialize<HistoricalDatasetEnvelope>(json, SerializerOptions);
        if (envelope is null)
        {
            return [];
        }

        if (envelope.SchemaVersion != CurrentSchemaVersion)
        {
            throw new NotSupportedException(
                $"Historical dataset schema version {envelope.SchemaVersion} is not supported (expected {CurrentSchemaVersion}).");
        }

        return envelope.Matches
            .Select(record => new MatchScore
            {
                League = record.League,
                HomeTeam = record.HomeTeam,
                AwayTeam = record.AwayTeam,
                Score = record.Score,
                MatchTime = record.MatchTimeUtc,
                BTTSLabel = record.BttsLabel,
                IsLive = record.IsLive
            })
            .ToList();
    }
}
