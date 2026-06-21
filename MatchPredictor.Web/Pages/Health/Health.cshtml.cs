using System.Text.Json;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Services;
using MatchPredictor.Infrastructure.Utils;
using MatchPredictor.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MatchPredictor.Web.Pages.Health;

public class Health : PageModel
{
    private const string AiScoreRuntimeEventName = ScrapingEventNames.AiScoreRuntime;
    private const string SofaScoreRuntimeEventName = ScrapingEventNames.SofaScoreRuntime;
    private static readonly IReadOnlyList<SignalDefinition> SignalDefinitions =
    [
        new("Data Sync", ScrapingEventNames.DataSync, TimeSpan.FromHours(8), true),
        new("Prediction Generation", ScrapingEventNames.PredictionGeneration, TimeSpan.FromHours(8), true),
        new("Recent Score Update", ScrapingEventNames.ScoreUpdateRecent, TimeSpan.FromMinutes(20), true),
        new("Score Backfill", ScrapingEventNames.ScoreUpdateBackfill, TimeSpan.FromHours(2), false),
        new("Daily Analysis", ScrapingEventNames.DailyAnalysis, TimeSpan.FromHours(30), true),
        new("Source Quality", ScrapingEventNames.SourceQuality, TimeSpan.FromHours(36), false)
    ];

    private readonly ApplicationDbContext _dbContext;
    private readonly IHealthQueryService _healthQueryService;
    private readonly OperationalStartupState _startupState;
    private readonly AiScoreSourceHealthTracker _aiScoreSourceHealthTracker;
    private readonly SofaScoreSourceHealthTracker _sofaScoreSourceHealthTracker;

    public Health(
        ApplicationDbContext dbContext,
        IHealthQueryService healthQueryService,
        OperationalStartupState startupState,
        AiScoreSourceHealthTracker aiScoreSourceHealthTracker,
        SofaScoreSourceHealthTracker sofaScoreSourceHealthTracker)
    {
        _dbContext = dbContext;
        _healthQueryService = healthQueryService;
        _startupState = startupState;
        _aiScoreSourceHealthTracker = aiScoreSourceHealthTracker;
        _sofaScoreSourceHealthTracker = sofaScoreSourceHealthTracker;
    }

    [BindProperty(SupportsGet = true)]
    public string? Format { get; set; }

    public OperationalHealthSnapshot Snapshot { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Snapshot = await BuildSnapshotAsync(ct);

        var acceptsJson = false;
        if (HttpContext is not null)
        {
            acceptsJson = Request.GetTypedHeaders().Accept?.Any(mediaType => string.Equals(
                mediaType.MediaType.Value,
                "application/json",
                StringComparison.OrdinalIgnoreCase)) == true;
        }

        if (string.Equals(Format, "json", StringComparison.OrdinalIgnoreCase) || acceptsJson)
        {
            return new JsonResult(Snapshot)
            {
                StatusCode = Snapshot.IsHealthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable
            };
        }

        return Page();
    }

    private async Task<OperationalHealthSnapshot> BuildSnapshotAsync(CancellationToken ct)
    {
        var nowLocal = DateTimeProvider.GetLocalTime();
        var today = DateOnly.FromDateTime(nowLocal);
        var eventNames = SignalDefinitions
            .Select(definition => definition.EventName)
            .Concat([AiScoreRuntimeEventName, SofaScoreRuntimeEventName])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var logs = await _healthQueryService.GetRecentScrapingLogsAsync(eventNames, limit: 500, ct);

        var groupedLogs = logs
            .GroupBy(log => log.EventName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var predictionsToday = await _dbContext.Predictions
            .AsNoTracking()
            .Where(prediction => prediction.MatchLocalDate == today && prediction.IsCurrentRevision)
            .CountAsync(ct);
        var livePredictionsToday = await _dbContext.Predictions
            .AsNoTracking()
            .Where(prediction => prediction.MatchLocalDate == today && prediction.IsCurrentRevision && prediction.IsLive)
            .CountAsync(ct);

        var signals = SignalDefinitions
            .Select(definition => BuildSignalStatus(definition, groupedLogs, nowLocal))
            .ToList();
        List<SourceQualityProfile> sourceQualityProfiles;
        List<SourceQualityProfile> weakestSourceProfiles;
        try
        {
            sourceQualityProfiles = await _dbContext.SourceQualityProfiles
                .AsNoTracking()
                .Where(profile => profile.LeagueKey == "all" && profile.TimeBucketKey == "all")
                .OrderByDescending(profile => profile.SourceName)
                .ToListAsync(ct);
            weakestSourceProfiles = await _dbContext.SourceQualityProfiles
                .AsNoTracking()
                .Where(profile =>
                    profile.LeagueKey != "all" &&
                    profile.TimeBucketKey != "all" &&
                    profile.SampleCount >= 4)
                .OrderBy(profile => profile.ReliabilityScore)
                .ThenByDescending(profile => profile.SampleCount)
                .Take(6)
                .ToListAsync(ct);
        }
        catch (PostgresException ex) when (IsMissingSourceQualityTable(ex))
        {
            sourceQualityProfiles = [];
            weakestSourceProfiles = [];
        }

        var predictionCoverageExpected = nowLocal.TimeOfDay >= TimeSpan.FromMinutes(45);
        var missingPredictions = predictionCoverageExpected && predictionsToday == 0;
        var criticalSignalIssue = signals.Any(signal => signal.IsCritical && signal.Level != HealthLevel.Healthy);

        return new OperationalHealthSnapshot
        {
            GeneratedAtLocal = nowLocal,
            BackgroundJobsEnabled = _startupState.BackgroundJobsEnabled,
            BrowserScrapingEnabled = _startupState.BrowserScrapingEnabled,
            DatabaseInitialized = _startupState.DatabaseInitialized,
            HangfireInitialized = _startupState.HangfireInitialized,
            RecurringJobsRegistered = _startupState.RecurringJobsRegistered,
            ExternalCronEnabled = _startupState.ExternalCronEnabled,
            StartupError = _startupState.InitializationError,
            StartedAtUtc = _startupState.StartedAtUtc,
            PredictionsToday = predictionsToday,
            LivePredictionsToday = livePredictionsToday,
            PredictionCoverageExpected = predictionCoverageExpected,
            Signals = signals,
            AiScoreRuntime = BuildAiScoreRuntimeStatus(ResolveAiScoreRuntimeSnapshot(groupedLogs, _aiScoreSourceHealthTracker.GetSnapshot())),
            SofaScoreRuntime = BuildSofaScoreRuntimeStatus(ResolveSofaScoreRuntimeSnapshot(groupedLogs, _sofaScoreSourceHealthTracker.GetSnapshot())),
            SourceQualityProfiles = sourceQualityProfiles
                .Select(BuildSourceQualitySummary)
                .ToList(),
            WeakestSourceProfiles = weakestSourceProfiles
                .Select(BuildSourceQualitySummary)
                .ToList(),
            IsHealthy = _startupState.DatabaseInitialized &&
                        _startupState.HangfireInitialized &&
                        (!_startupState.BackgroundJobsEnabled ||
                         _startupState.RecurringJobsRegistered ||
                         _startupState.ExternalCronEnabled) &&
                        string.IsNullOrWhiteSpace(_startupState.InitializationError) &&
                        !criticalSignalIssue &&
                        !missingPredictions
        };
    }

    private static HealthSignalStatus BuildSignalStatus(
        SignalDefinition definition,
        IReadOnlyDictionary<string, List<ScrapingLog>> groupedLogs,
        DateTime nowLocal)
    {
        groupedLogs.TryGetValue(definition.EventName, out var logsForSignal);
        logsForSignal ??= [];

        var latestLog = logsForSignal.FirstOrDefault();
        var lastSuccess = logsForSignal.FirstOrDefault(log => string.Equals(log.Status, "Success", StringComparison.OrdinalIgnoreCase));
        var latestLocalTimestamp = latestLog is null ? (DateTime?)null : DateTimeProvider.ConvertUtcToLocal(latestLog.Timestamp);
        var lastSuccessLocalTimestamp = lastSuccess is null ? (DateTime?)null : DateTimeProvider.ConvertUtcToLocal(lastSuccess.Timestamp);

        var level = HealthLevel.Healthy;
        if (lastSuccessLocalTimestamp is null)
        {
            level = HealthLevel.Missing;
        }
        else if (latestLog is not null &&
                 string.Equals(latestLog.Status, "Failed", StringComparison.OrdinalIgnoreCase) &&
                 latestLog.Timestamp >= (lastSuccess?.Timestamp ?? DateTime.MinValue))
        {
            level = HealthLevel.Failed;
        }
        else if (nowLocal - lastSuccessLocalTimestamp.Value > definition.MaxLag)
        {
            level = HealthLevel.Stale;
        }

        return new HealthSignalStatus
        {
            Name = definition.Name,
            EventName = definition.EventName,
            IsCritical = definition.IsCritical,
            MaxLagMinutes = (int)definition.MaxLag.TotalMinutes,
            Level = level,
            LatestStatus = latestLog?.Status,
            LatestMessage = latestLog?.Message,
            LastSeenLocal = latestLocalTimestamp,
            LastSuccessfulLocal = lastSuccessLocalTimestamp,
            LagMinutes = lastSuccessLocalTimestamp.HasValue
                ? Math.Round((nowLocal - lastSuccessLocalTimestamp.Value).TotalMinutes, 1)
                : null
        };
    }

    private static SourceQualitySummary BuildSourceQualitySummary(SourceQualityProfile profile)
    {
        return new SourceQualitySummary
        {
            SourceName = profile.SourceName,
            LeagueLabel = profile.LeagueLabel,
            TimeBucketLabel = profile.TimeBucketLabel,
            SampleCount = profile.SampleCount,
            FinishedCoverageRate = profile.FinishedCoverageRate,
            ExactScoreMatchRate = profile.ExactScoreMatchRate,
            LiveOnlyRate = profile.LiveOnlyRate,
            ReliabilityScore = profile.ReliabilityScore,
            AverageKickoffOffsetMinutes = profile.AverageKickoffOffsetMinutes
        };
    }

    private static AiScoreRuntimeStatus BuildAiScoreRuntimeStatus(AiScoreSourceHealthSnapshot snapshot)
    {
        return new AiScoreRuntimeStatus
        {
            Status = snapshot.Status,
            LastStage = snapshot.LastStage,
            LastDetail = snapshot.LastDetail,
            LastAttemptLocal = snapshot.LastAttemptUtc.HasValue
                ? DateTimeProvider.ConvertUtcToLocal(snapshot.LastAttemptUtc.Value)
                : null,
            LastSuccessLocal = snapshot.LastSuccessUtc.HasValue
                ? DateTimeProvider.ConvertUtcToLocal(snapshot.LastSuccessUtc.Value)
                : null,
            LastMatchCount = snapshot.LastMatchCount,
            LastFallbackMatchCount = snapshot.LastFallbackMatchCount,
            LastSupplementMatchCount = snapshot.LastSupplementMatchCount,
            IsCoolingDown = snapshot.IsCoolingDown,
            CooldownUntilLocal = snapshot.CooldownUntilUtc.HasValue
                ? DateTimeProvider.ConvertUtcToLocal(snapshot.CooldownUntilUtc.Value)
                : null
        };
    }

    private static AiScoreSourceHealthSnapshot ResolveAiScoreRuntimeSnapshot(
        IReadOnlyDictionary<string, List<ScrapingLog>> groupedLogs,
        AiScoreSourceHealthSnapshot fallbackSnapshot)
    {
        return TryDeserializeRuntimeSnapshot(groupedLogs, AiScoreRuntimeEventName, fallbackSnapshot);
    }

    private static SofaScoreRuntimeStatus BuildSofaScoreRuntimeStatus(SofaScoreSourceHealthSnapshot snapshot)
    {
        var successDenominator = Math.Max(1, snapshot.TotalAttempts);
        return new SofaScoreRuntimeStatus
        {
            Status = snapshot.Status,
            LastStage = snapshot.LastStage,
            LastDetail = snapshot.LastDetail,
            LastAttemptLocal = snapshot.LastAttemptUtc.HasValue
                ? DateTimeProvider.ConvertUtcToLocal(snapshot.LastAttemptUtc.Value)
                : null,
            LastSuccessLocal = snapshot.LastSuccessUtc.HasValue
                ? DateTimeProvider.ConvertUtcToLocal(snapshot.LastSuccessUtc.Value)
                : null,
            LastMatchCount = snapshot.LastMatchCount,
            LastCandidateUrlCount = snapshot.LastCandidateUrlCount,
            LastPageFetchCount = snapshot.LastPageFetchCount,
            TotalAttempts = snapshot.TotalAttempts,
            TotalSuccesses = snapshot.TotalSuccesses,
            TotalBlocked = snapshot.TotalBlocked,
            TotalEmpty = snapshot.TotalEmpty,
            TotalFailures = snapshot.TotalFailures,
            TotalPageFetches = snapshot.TotalPageFetches,
            SuccessRate = snapshot.TotalSuccesses / (double)successDenominator,
            BlockRate = snapshot.TotalBlocked / (double)successDenominator
        };
    }

    private static SofaScoreSourceHealthSnapshot ResolveSofaScoreRuntimeSnapshot(
        IReadOnlyDictionary<string, List<ScrapingLog>> groupedLogs,
        SofaScoreSourceHealthSnapshot fallbackSnapshot)
    {
        return TryDeserializeRuntimeSnapshot(groupedLogs, SofaScoreRuntimeEventName, fallbackSnapshot);
    }

    private static TSnapshot TryDeserializeRuntimeSnapshot<TSnapshot>(
        IReadOnlyDictionary<string, List<ScrapingLog>> groupedLogs,
        string eventName,
        TSnapshot fallbackSnapshot)
    {
        if (!groupedLogs.TryGetValue(eventName, out var logsForSignal) || logsForSignal.Count == 0)
        {
            return fallbackSnapshot;
        }

        foreach (var log in logsForSignal)
        {
            if (string.IsNullOrWhiteSpace(log.Message))
            {
                continue;
            }

            try
            {
                var snapshot = JsonSerializer.Deserialize<TSnapshot>(log.Message);
                if (snapshot is not null)
                {
                    return snapshot;
                }
            }
            catch (JsonException)
            {
            }
        }

        return fallbackSnapshot;
    }

    private static bool IsMissingSourceQualityTable(PostgresException ex)
    {
        return ex.SqlState == PostgresErrorCodes.UndefinedTable &&
               string.Equals(ex.TableName, "SourceQualityProfiles", StringComparison.Ordinal);
    }

    private sealed record SignalDefinition(string Name, string EventName, TimeSpan MaxLag, bool IsCritical);
}

public sealed class OperationalHealthSnapshot
{
    public DateTime GeneratedAtLocal { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public bool BackgroundJobsEnabled { get; init; }
    public bool BrowserScrapingEnabled { get; init; }
    public bool DatabaseInitialized { get; init; }
    public bool HangfireInitialized { get; init; }
    public bool RecurringJobsRegistered { get; init; }
    public bool ExternalCronEnabled { get; init; }
    public string? StartupError { get; init; }
    public int PredictionsToday { get; init; }
    public int LivePredictionsToday { get; init; }
    public bool PredictionCoverageExpected { get; init; }
    public bool IsHealthy { get; init; }
    public List<HealthSignalStatus> Signals { get; init; } = [];
    public AiScoreRuntimeStatus AiScoreRuntime { get; init; } = new();
    public SofaScoreRuntimeStatus SofaScoreRuntime { get; init; } = new();
    public List<SourceQualitySummary> SourceQualityProfiles { get; init; } = [];
    public List<SourceQualitySummary> WeakestSourceProfiles { get; init; } = [];
}

public sealed class HealthSignalStatus
{
    public string Name { get; init; } = string.Empty;
    public string EventName { get; init; } = string.Empty;
    public bool IsCritical { get; init; }
    public int MaxLagMinutes { get; init; }
    public HealthLevel Level { get; init; }
    public string? LatestStatus { get; init; }
    public string? LatestMessage { get; init; }
    public DateTime? LastSeenLocal { get; init; }
    public DateTime? LastSuccessfulLocal { get; init; }
    public double? LagMinutes { get; init; }
}

public enum HealthLevel
{
    Healthy,
    Stale,
    Failed,
    Missing
}

public sealed class AiScoreRuntimeStatus
{
    public string Status { get; init; } = "Idle";
    public string? LastStage { get; init; }
    public string? LastDetail { get; init; }
    public DateTime? LastAttemptLocal { get; init; }
    public DateTime? LastSuccessLocal { get; init; }
    public int LastMatchCount { get; init; }
    public int LastFallbackMatchCount { get; init; }
    public int LastSupplementMatchCount { get; init; }
    public bool IsCoolingDown { get; init; }
    public DateTime? CooldownUntilLocal { get; init; }
}

public sealed class SofaScoreRuntimeStatus
{
    public string Status { get; init; } = "Idle";
    public string? LastStage { get; init; }
    public string? LastDetail { get; init; }
    public DateTime? LastAttemptLocal { get; init; }
    public DateTime? LastSuccessLocal { get; init; }
    public int LastMatchCount { get; init; }
    public int LastCandidateUrlCount { get; init; }
    public int LastPageFetchCount { get; init; }
    public int TotalAttempts { get; init; }
    public int TotalSuccesses { get; init; }
    public int TotalBlocked { get; init; }
    public int TotalEmpty { get; init; }
    public int TotalFailures { get; init; }
    public int TotalPageFetches { get; init; }
    public double SuccessRate { get; init; }
    public double BlockRate { get; init; }
}

public sealed class SourceQualitySummary
{
    public string SourceName { get; init; } = string.Empty;
    public string LeagueLabel { get; init; } = string.Empty;
    public string TimeBucketLabel { get; init; } = string.Empty;
    public int SampleCount { get; init; }
    public double FinishedCoverageRate { get; init; }
    public double ExactScoreMatchRate { get; init; }
    public double LiveOnlyRate { get; init; }
    public double ReliabilityScore { get; init; }
    public double AverageKickoffOffsetMinutes { get; init; }
}
