using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Utils;
using MatchPredictor.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MatchPredictor.Web.Pages.Health;

public class Health : PageModel
{
    private static readonly IReadOnlyList<SignalDefinition> SignalDefinitions =
    [
        new("Data Sync", "data_sync", TimeSpan.FromHours(8), true),
        new("Prediction Generation", "prediction_generation", TimeSpan.FromHours(8), true),
        new("Recent Score Update", "score_update_recent", TimeSpan.FromMinutes(20), true),
        new("Score Backfill", "score_update_backfill", TimeSpan.FromHours(2), false),
        new("Daily Analysis", "daily_analysis", TimeSpan.FromHours(30), true),
        new("Source Quality", "source_quality", TimeSpan.FromHours(36), false)
    ];

    private readonly ApplicationDbContext _dbContext;
    private readonly OperationalStartupState _startupState;

    public Health(ApplicationDbContext dbContext, OperationalStartupState startupState)
    {
        _dbContext = dbContext;
        _startupState = startupState;
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
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var logs = await _dbContext.ScrapingLogs
            .AsNoTracking()
            .Where(log => eventNames.Contains(log.EventName))
            .OrderByDescending(log => log.Timestamp)
            .ToListAsync(ct);

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
            DatabaseInitialized = _startupState.DatabaseInitialized,
            HangfireInitialized = _startupState.HangfireInitialized,
            RecurringJobsRegistered = _startupState.RecurringJobsRegistered,
            StartupError = _startupState.InitializationError,
            StartedAtUtc = _startupState.StartedAtUtc,
            PredictionsToday = predictionsToday,
            LivePredictionsToday = livePredictionsToday,
            PredictionCoverageExpected = predictionCoverageExpected,
            Signals = signals,
            SourceQualityProfiles = sourceQualityProfiles
                .Select(BuildSourceQualitySummary)
                .ToList(),
            WeakestSourceProfiles = weakestSourceProfiles
                .Select(BuildSourceQualitySummary)
                .ToList(),
            IsHealthy = _startupState.DatabaseInitialized &&
                        _startupState.HangfireInitialized &&
                        _startupState.RecurringJobsRegistered &&
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
    public bool DatabaseInitialized { get; init; }
    public bool HangfireInitialized { get; init; }
    public bool RecurringJobsRegistered { get; init; }
    public string? StartupError { get; init; }
    public int PredictionsToday { get; init; }
    public int LivePredictionsToday { get; init; }
    public bool PredictionCoverageExpected { get; init; }
    public bool IsHealthy { get; init; }
    public List<HealthSignalStatus> Signals { get; init; } = [];
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
