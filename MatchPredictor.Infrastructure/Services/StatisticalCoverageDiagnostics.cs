using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatchPredictor.Infrastructure.Services;

public sealed class StatisticalCoverageDiagnostics : IStatisticalCoverageDiagnostics
{
    private const int MatchScoreLookbackDays = 540;
    private const int ForecastLookbackDays = 180;

    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<StatisticalCoverageDiagnostics> _logger;

    public StatisticalCoverageDiagnostics(
        ApplicationDbContext dbContext,
        ILogger<StatisticalCoverageDiagnostics> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task RunNightlyDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var report = await MeasureAsync(cancellationToken);
        _logger.LogInformation(
            "Statistical coverage: MatchScores={MatchScoreCount} (lookback {ScoreDays}d), teams={Teams}, settledForecasts={Forecasts}, DixonColesCoverage={SignalCoverage:P1}, formSnapshots={FormCoverage:P1}, TeamMatchStats={StatsCount}, xGCoverage={XgCoverage:P1}, schemaReady={SchemaReady}. {Notes}",
            report.MatchScoreCount,
            report.MatchScoreLookbackDays,
            report.DistinctTeamsInScores,
            report.SettledForecastCount,
            report.StatisticalSignalCoverage,
            report.FormFeatureCoverage,
            report.TeamMatchStatsCount,
            report.XgCoverage,
            report.XgFeatureSchemaReady,
            report.Notes);
    }

    public async Task<StatisticalCoverageReport> MeasureAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = DateTime.UtcNow;
        var scoreStart = nowUtc.AddDays(-MatchScoreLookbackDays);
        var forecastStart = nowUtc.AddDays(-ForecastLookbackDays);

        var scores = await _dbContext.MatchScores
            .AsNoTracking()
            .Where(score => !score.IsLive && score.MatchTime >= scoreStart)
            .Select(score => new { score.HomeTeam, score.AwayTeam })
            .ToListAsync(cancellationToken);

        var distinctTeams = scores
            .SelectMany(score => new[] { score.HomeTeam, score.AwayTeam })
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var settledForecasts = await _dbContext.ForecastObservations
            .AsNoTracking()
            .Where(forecast => forecast.IsSettled && forecast.CreatedAt >= forecastStart)
            .Select(forecast => new { forecast.FeatureContributionsJson })
            .ToListAsync(cancellationToken);

        var withStatistical = settledForecasts.Count(forecast =>
            !string.IsNullOrWhiteSpace(forecast.FeatureContributionsJson) &&
            forecast.FeatureContributionsJson.Contains("DixonColes", StringComparison.OrdinalIgnoreCase));

        var snapshots = await _dbContext.FixtureFeatureSnapshots
            .AsNoTracking()
            .Where(snapshot => snapshot.CapturedAtUtc >= forecastStart)
            .Select(snapshot => new
            {
                snapshot.HomeFormPointsPerMatch,
                snapshot.AwayFormPointsPerMatch
            })
            .ToListAsync(cancellationToken);

        var withForm = snapshots.Count(snapshot =>
            snapshot.HomeFormPointsPerMatch.HasValue || snapshot.AwayFormPointsPerMatch.HasValue);

        var teamStatsCount = await _dbContext.TeamMatchStats.CountAsync(cancellationToken);
        var teamStatsWithXg = await _dbContext.TeamMatchStats
            .CountAsync(row => row.ExpectedGoalsFor != null, cancellationToken);

        var xgCoverage = teamStatsCount == 0 ? 0.0 : teamStatsWithXg / (double)teamStatsCount;
        var signalCoverage = settledForecasts.Count == 0
            ? 0.0
            : withStatistical / (double)settledForecasts.Count;
        var formCoverage = snapshots.Count == 0 ? 0.0 : withForm / (double)snapshots.Count;

        return new StatisticalCoverageReport
        {
            GeneratedAtUtc = nowUtc,
            MatchScoreLookbackDays = MatchScoreLookbackDays,
            MatchScoreCount = scores.Count,
            DistinctTeamsInScores = distinctTeams,
            ForecastLookbackDays = ForecastLookbackDays,
            SettledForecastCount = settledForecasts.Count,
            ForecastsWithStatisticalSignal = withStatistical,
            StatisticalSignalCoverage = signalCoverage,
            FixtureFeatureSnapshotCount = snapshots.Count,
            SnapshotsWithNonNullForm = withForm,
            FormFeatureCoverage = formCoverage,
            TeamMatchStatsCount = teamStatsCount,
            TeamMatchStatsWithXg = teamStatsWithXg,
            XgCoverage = xgCoverage,
            XgFeatureSchemaReady = xgCoverage >= IMlXgFeatureReadiness.MinimumXgCoverage && teamStatsCount >= 50,
            Notes = teamStatsWithXg == 0
                ? "SofaScore ingest has no xG fields; TeamMatchStats xG is null until a licensed current-season feed is connected. Do not bump LightGBM FeatureColumns yet."
                : $"xG coverage {xgCoverage:P1}; schema bump allowed only at >= {IMlXgFeatureReadiness.MinimumXgCoverage:P0}."
        };
    }
}
