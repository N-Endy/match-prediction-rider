using System.Globalization;
using System.Text.Json;
using Hangfire;
using MatchPredictor.Application.Helpers;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Domain.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MatchPredictor.Application.Services;

public partial class AnalyzerService
{
    private async Task<SourceQualityRebuildResult> RebuildSourceQualityProfilesAsync(int lookbackDays = 30)
    {
        var today = DateOnly.FromDateTime(DateTimeProvider.GetLocalTime());
        var earliestSettlementDate = today.AddDays(-Math.Max(lookbackDays, 1));
        var startOfWindowUtc = DateTimeProvider.ConvertLocalToUtc(
            earliestSettlementDate.ToDateTime(new TimeOnly(0, 0), DateTimeKind.Unspecified));
        var endOfWindowUtc = DateTimeProvider.ConvertLocalToUtc(
            today.AddDays(1).ToDateTime(new TimeOnly(0, 0), DateTimeKind.Unspecified));

        var settledPredictions = await _dbContext.Predictions
            .AsNoTracking()
            .Where(prediction =>
                prediction.IsCurrentRevision &&
                prediction.MatchLocalDate >= earliestSettlementDate &&
                !prediction.IsLive &&
                !string.IsNullOrWhiteSpace(prediction.ActualScore))
            .ToListAsync();

        if (settledPredictions.Count == 0)
        {
            var clearedEmptyProfiles = await ReplaceSourceQualityProfilesAsync([]);
            return clearedEmptyProfiles
                ? new SourceQualityRebuildResult(true, 0, "No settled predictions were available in the source-quality lookback window.")
                : SourceQualityRebuildResult.Skipped("The SourceQualityProfiles table is missing.");
        }

        var fixtures = BuildSettlementFixtureGroups(settledPredictions, []);
        var flashScores = await _dbContext.MatchScores
            .AsNoTracking()
            .Where(score => score.MatchTime >= startOfWindowUtc && score.MatchTime < endOfWindowUtc)
            .ToListAsync();
        var aiScores = await _dbContext.AiScoreMatchScores
            .AsNoTracking()
            .Where(score => score.MatchTime >= startOfWindowUtc && score.MatchTime < endOfWindowUtc)
            .ToListAsync();
        List<SofaScoreMatchScore> sofaScores;
        try
        {
            sofaScores = await _dbContext.SofaScoreMatchScores
                .AsNoTracking()
                .Where(score => score.MatchTime >= startOfWindowUtc && score.MatchTime < endOfWindowUtc)
                .ToListAsync();
        }
        catch (PostgresException ex) when (IsMissingSofaScoreTable(ex))
        {
            _logger.LogWarning(
                "Skipping source quality profile refresh because the SofaScoreMatchScores table is missing. Apply the latest EF migration to enable SofaScore source-quality training.");
            return SourceQualityRebuildResult.Skipped("The SofaScoreMatchScores table is missing.");
        }

        var flashFinishedIndex = BuildExactFinishedCandidateIndex(
            flashScores.Where(score => !score.IsLive),
            score => score.HomeTeam,
            score => score.AwayTeam,
            score => score.MatchTime,
            score => score.League);
        var flashLiveIndex = BuildExactFinishedCandidateIndex(
            flashScores.Where(score => score.IsLive),
            score => score.HomeTeam,
            score => score.AwayTeam,
            score => score.MatchTime,
            score => score.League);
        var aiFinishedIndex = BuildExactFinishedCandidateIndex(
            aiScores.Where(score => !score.IsLive),
            score => score.HomeTeam,
            score => score.AwayTeam,
            score => score.MatchTime,
            score => score.League);
        var aiLiveIndex = BuildExactFinishedCandidateIndex(
            aiScores.Where(score => score.IsLive),
            score => score.HomeTeam,
            score => score.AwayTeam,
            score => score.MatchTime,
            score => score.League);
        var sofaFinishedIndex = BuildExactFinishedCandidateIndex(
            sofaScores.Where(score => !score.IsLive),
            score => score.HomeTeam,
            score => score.AwayTeam,
            score => score.MatchTime,
            score => score.League);
        var sofaLiveIndex = BuildExactFinishedCandidateIndex(
            sofaScores.Where(score => score.IsLive),
            score => score.HomeTeam,
            score => score.AwayTeam,
            score => score.MatchTime,
            score => score.League);

        var accumulators = new Dictionary<(string SourceName, string LeagueKey, string TimeBucketKey), SourceQualityAccumulator>();

        foreach (var fixture in fixtures)
        {
            var settledScore = fixture.Predictions
                .Select(prediction => NormalizeSettledScore(prediction.ActualScore))
                .FirstOrDefault(score => !string.IsNullOrWhiteSpace(score));

            if (string.IsNullOrWhiteSpace(settledScore))
            {
                continue;
            }

            RecordSourceQualitySample(
                accumulators,
                "FlashScore",
                fixture,
                FindExactFinishedSourceCandidate(
                    fixture,
                    flashFinishedIndex,
                    score => score.MatchTime,
                    score => score.League,
                    score => score.Score),
                FindLatestExactLiveSourceCandidate(
                    fixture,
                    flashLiveIndex,
                    score => score.MatchTime),
                score => score.Score,
                score => score.MatchTime,
                settledScore);

            RecordSourceQualitySample(
                accumulators,
                "AiScore",
                fixture,
                FindExactFinishedSourceCandidate(
                    fixture,
                    aiFinishedIndex,
                    score => score.MatchTime,
                    score => score.League,
                    score => score.Score),
                FindLatestExactLiveSourceCandidate(
                    fixture,
                    aiLiveIndex,
                    score => score.MatchTime),
                score => score.Score,
                score => score.MatchTime,
                settledScore);

            RecordSourceQualitySample(
                accumulators,
                "SofaScore",
                fixture,
                FindExactFinishedSourceCandidate(
                    fixture,
                    sofaFinishedIndex,
                    score => score.MatchTime,
                    score => score.League,
                    score => score.Score),
                FindLatestExactLiveSourceCandidate(
                    fixture,
                    sofaLiveIndex,
                    score => score.MatchTime),
                score => score.Score,
                score => score.MatchTime,
                settledScore);
        }

        var nowUtc = DateTime.UtcNow;
        var profiles = accumulators.Values
            .Select(accumulator => accumulator.ToProfile(nowUtc))
            .Where(profile => profile.SampleCount > 0)
            .ToList();

        var replacedProfiles = await ReplaceSourceQualityProfilesAsync(profiles);
        return replacedProfiles
            ? new SourceQualityRebuildResult(true, profiles.Count, "Source quality profiles rebuilt successfully.")
            : SourceQualityRebuildResult.Skipped("The SourceQualityProfiles table is missing.");
    }

    private void RecordSourceQualitySample<T>(
        IDictionary<(string SourceName, string LeagueKey, string TimeBucketKey), SourceQualityAccumulator> accumulators,
        string sourceName,
        SettlementFixtureGroup fixture,
        T? finishedCandidate,
        T? liveCandidate,
        Func<T, string?> scoreSelector,
        Func<T, DateTime?> matchTimeSelector,
        string settledScore)
        where T : class
    {
        var exactLeagueKey = NormalizeLeagueKey(fixture.League);
        var timeBucket = GetTimeBucket(fixture.ScheduledMatchTimeUtc);
        var applicableKeys = new[]
        {
            (SourceName: sourceName, LeagueKey: exactLeagueKey, TimeBucketKey: timeBucket.Key),
            (SourceName: sourceName, LeagueKey: exactLeagueKey, TimeBucketKey: "all"),
            (SourceName: sourceName, LeagueKey: "all", TimeBucketKey: timeBucket.Key),
            (SourceName: sourceName, LeagueKey: "all", TimeBucketKey: "all")
        };

        foreach (var key in applicableKeys)
        {
            if (!accumulators.TryGetValue(key, out var accumulator))
            {
                accumulator = new SourceQualityAccumulator(
                    key.SourceName,
                    key.LeagueKey,
                    key.LeagueKey == "all" ? "All Leagues" : fixture.League,
                    key.TimeBucketKey,
                    key.TimeBucketKey == "all" ? "All Kickoffs" : timeBucket.Label);
                accumulators[key] = accumulator;
            }

            accumulator.RecordSample(
                finishedCandidate is not null,
                finishedCandidate is not null &&
                string.Equals(NormalizeSettledScore(scoreSelector(finishedCandidate)), settledScore, StringComparison.Ordinal),
                finishedCandidate is null && liveCandidate is not null,
                finishedCandidate is not null && fixture.ScheduledMatchTimeUtc.HasValue && matchTimeSelector(finishedCandidate).HasValue
                    ? Math.Abs((matchTimeSelector(finishedCandidate)!.Value - fixture.ScheduledMatchTimeUtc.Value).TotalMinutes)
                    : null);
        }
    }

    private async Task<bool> ReplaceSourceQualityProfilesAsync(IReadOnlyCollection<SourceQualityProfile> profiles)
    {
        try
        {
            await _dbContext.SourceQualityProfiles.ExecuteDeleteAsync();
        }
        catch (InvalidOperationException)
        {
            var existingProfiles = await _dbContext.SourceQualityProfiles.ToListAsync();
            _dbContext.SourceQualityProfiles.RemoveRange(existingProfiles);
        }
        catch (PostgresException ex) when (IsMissingSourceQualityTable(ex))
        {
            _logger.LogWarning(
                "Skipping source quality profile refresh because the SourceQualityProfiles table is missing. Apply the latest EF migration to enable this feature.");
            return false;
        }

        if (profiles.Count > 0)
        {
            await _dbContext.SourceQualityProfiles.AddRangeAsync(profiles);
        }

        await _dbContext.SaveChangesAsync();
        return true;
    }
    private static double GetSourceQualityReliability(
        IReadOnlyDictionary<(string SourceName, string LeagueKey, string TimeBucketKey), SourceQualityProfile> sourceQualityLookup,
        string sourceName,
        string? league,
        DateTime? kickoffUtc)
    {
        if (sourceQualityLookup.Count == 0)
        {
            return 0.5;
        }

        var normalizedSource = NormalizeSourceName(sourceName);
        var leagueKey = NormalizeLeagueKey(league);
        var timeBucketKey = GetTimeBucket(kickoffUtc).Key;

        foreach (var key in new[]
                 {
                     (normalizedSource, leagueKey, timeBucketKey),
                     (normalizedSource, leagueKey, "all"),
                     (normalizedSource, "all", timeBucketKey),
                     (normalizedSource, "all", "all")
                 })
        {
            if (sourceQualityLookup.TryGetValue(key, out var profile) && profile.SampleCount >= 4)
            {
                return Math.Clamp(profile.ReliabilityScore, 0.0, 1.0);
            }
        }

        return 0.5;
    }

    private static (string Key, string Label) GetTimeBucket(DateTime? kickoffUtc)
    {
        if (!kickoffUtc.HasValue)
        {
            return ("all", "All Kickoffs");
        }

        var localHour = DateTimeProvider.ConvertUtcToLocal(kickoffUtc.Value).Hour;
        return localHour switch
        {
            < 6 => ("night", "00:00-05:59"),
            < 12 => ("morning", "06:00-11:59"),
            < 18 => ("afternoon", "12:00-17:59"),
            _ => ("evening", "18:00-23:59")
        };
    }

    private static string NormalizeSourceName(string sourceName)
    {
        return string.IsNullOrWhiteSpace(sourceName)
            ? "unknown"
            : sourceName.Trim().ToLowerInvariant();
    }

    private static string NormalizeLeagueKey(string? league)
    {
        return string.IsNullOrWhiteSpace(league)
            ? "unknown"
            : league.Trim().ToLowerInvariant();
    }

    private static bool IsMissingSourceQualityTable(PostgresException ex)
    {
        return IsMissingTable(ex, "SourceQualityProfiles");
    }

    private static bool IsMissingSofaScoreTable(PostgresException ex)
    {
        return IsMissingTable(ex, "SofaScoreMatchScores");
    }

    private static bool IsMissingTable(PostgresException ex, string tableName)
    {
        return ex.SqlState == PostgresErrorCodes.UndefinedTable &&
               string.Equals(ex.TableName, tableName, StringComparison.Ordinal);
    }
    private sealed class SourceQualityAccumulator
    {
        private double _kickoffOffsetTotal;
        private int _kickoffOffsetCount;

        public SourceQualityAccumulator(
            string sourceName,
            string leagueKey,
            string leagueLabel,
            string timeBucketKey,
            string timeBucketLabel)
        {
            SourceName = sourceName;
            LeagueKey = leagueKey;
            LeagueLabel = leagueLabel;
            TimeBucketKey = timeBucketKey;
            TimeBucketLabel = timeBucketLabel;
        }

        public string SourceName { get; }
        public string LeagueKey { get; }
        public string LeagueLabel { get; }
        public string TimeBucketKey { get; }
        public string TimeBucketLabel { get; }
        public int SampleCount { get; private set; }
        public int FinishedCoverageCount { get; private set; }
        public int ExactScoreMatchCount { get; private set; }
        public int LiveOnlyCount { get; private set; }

        public void RecordSample(bool hasFinishedSource, bool exactScoreMatch, bool isLiveOnly, double? kickoffOffsetMinutes)
        {
            SampleCount++;

            if (hasFinishedSource)
            {
                FinishedCoverageCount++;
            }

            if (exactScoreMatch)
            {
                ExactScoreMatchCount++;
            }

            if (isLiveOnly)
            {
                LiveOnlyCount++;
            }

            if (kickoffOffsetMinutes.HasValue)
            {
                _kickoffOffsetTotal += kickoffOffsetMinutes.Value;
                _kickoffOffsetCount++;
            }
        }

        public SourceQualityProfile ToProfile(DateTime updatedAtUtc)
        {
            var coverageRate = SampleCount > 0 ? FinishedCoverageCount / (double)SampleCount : 0.0;
            var matchRate = FinishedCoverageCount > 0 ? ExactScoreMatchCount / (double)FinishedCoverageCount : 0.0;
            var liveOnlyRate = SampleCount > 0 ? LiveOnlyCount / (double)SampleCount : 0.0;
            var avgOffset = _kickoffOffsetCount > 0 ? _kickoffOffsetTotal / _kickoffOffsetCount : 0.0;
            var offsetPenalty = Math.Clamp(avgOffset / 180.0, 0.0, 1.0);
            var reliability = Math.Clamp(
                (matchRate * 0.55) +
                (coverageRate * 0.30) +
                ((1.0 - liveOnlyRate) * 0.15) -
                (offsetPenalty * 0.10),
                0.0,
                1.0);

            return new SourceQualityProfile
            {
                SourceName = SourceName,
                LeagueKey = LeagueKey,
                LeagueLabel = LeagueLabel,
                TimeBucketKey = TimeBucketKey,
                TimeBucketLabel = TimeBucketLabel,
                SampleCount = SampleCount,
                FinishedCoverageCount = FinishedCoverageCount,
                ExactScoreMatchCount = ExactScoreMatchCount,
                LiveOnlyCount = LiveOnlyCount,
                AverageKickoffOffsetMinutes = avgOffset,
                ReliabilityScore = reliability,
                LastUpdated = updatedAtUtc
            };
        }
    }

    private sealed record SourceQualityRebuildResult(bool Completed, int ProfileCount, string StatusMessage)
    {
        public static SourceQualityRebuildResult Skipped(string reason) => new(false, 0, reason);
    }

}
