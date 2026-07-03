using System.Globalization;
using System.Text.Json;
using Hangfire;
using MatchPredictor.Application.Helpers;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Services;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MatchPredictor.Application.Services;

public partial class AnalyzerService : IAnalyzerService
{
    private const int RecentScoreUpdaterLookbackDays = 1;
    private const int HistoricalScoreBackfillLookbackDays = 14;
    private const double ExactFinishedRepairWindowMinutes = 65d;
    private const double ExtendedExactFinishedRepairWindowMinutes = 240d;
    private static readonly TimeSpan FutureFixtureSettlementTolerance = TimeSpan.Zero;
    private static readonly TimeSpan CurrentRevisionKickoffGrace = TimeSpan.FromMinutes(5);
    private const string DataSyncEventName = ScrapingEventNames.DataSync;
    private const string PredictionGenerationEventName = ScrapingEventNames.PredictionGeneration;
    private const string DailyAnalysisEventName = ScrapingEventNames.DailyAnalysis;
    private const string SourceQualityEventName = ScrapingEventNames.SourceQuality;
    private const string ClosingLineSnapshotEventName = ScrapingEventNames.ClosingLineSnapshot;
    private const string AiScoreRuntimeEventName = ScrapingEventNames.AiScoreRuntime;
    private const string SofaScoreRuntimeEventName = ScrapingEventNames.SofaScoreRuntime;
    internal const string AnalyzerJobResource = "matchpredictor-analyzer";

    private readonly IDataAnalyzerService _dataAnalyzerService;
    private readonly IWebScraperService _webScraperService;
    private readonly ApplicationDbContext _dbContext;
    private readonly IExtractFromExcel _excelExtract;
    private readonly ILogger<AnalyzerService> _logger;
    private readonly IRegressionPredictorService _regressionPredictorService;
    private readonly ICalibrationService _calibrationService;
    private readonly IProbabilityCorrectionService _probabilityCorrectionService;
    private readonly IThresholdTuningService _thresholdTuningService;
    private readonly ISourceMarketPricingService _sourceMarketPricingService;
    private readonly IFixtureFeatureService? _fixtureFeatureService;
    private readonly AiScoreSourceHealthTracker _aiScoreSourceHealthTracker;
    private readonly SofaScoreSourceHealthTracker _sofaScoreSourceHealthTracker;
    private readonly PredictionSettings _predictionSettings;
    private readonly ILearningLoopService _learningLoopService;

    public AnalyzerService(
        IDataAnalyzerService dataAnalyzerService,
        IWebScraperService webScraperService,
        ApplicationDbContext dbContext,
        IExtractFromExcel excelExtract,
        IRegressionPredictorService regressionPredictorService,
        ICalibrationService calibrationService,
        IProbabilityCorrectionService probabilityCorrectionService,
        IThresholdTuningService thresholdTuningService,
        ISourceMarketPricingService sourceMarketPricingService,
        AiScoreSourceHealthTracker aiScoreSourceHealthTracker,
        SofaScoreSourceHealthTracker sofaScoreSourceHealthTracker,
        IOptions<PredictionSettings> predictionOptions,
        ILogger<AnalyzerService> logger,
        ILearningLoopService? learningLoopService = null,
        IFixtureFeatureService? fixtureFeatureService = null)
    {
        _dataAnalyzerService = dataAnalyzerService;
        _webScraperService = webScraperService;
        _dbContext = dbContext;
        _excelExtract = excelExtract;
        _regressionPredictorService = regressionPredictorService;
        _calibrationService = calibrationService;
        _probabilityCorrectionService = probabilityCorrectionService;
        _thresholdTuningService = thresholdTuningService;
        _sourceMarketPricingService = sourceMarketPricingService;
        _fixtureFeatureService = fixtureFeatureService;
        _aiScoreSourceHealthTracker = aiScoreSourceHealthTracker;
        _sofaScoreSourceHealthTracker = sofaScoreSourceHealthTracker;
        _predictionSettings = predictionOptions.Value;
        _logger = logger;
        _learningLoopService = learningLoopService ?? new LearningLoopService(
            calibrationService,
            probabilityCorrectionService,
            thresholdTuningService,
            dbContext,
            NullLogger<LearningLoopService>.Instance);
    }
    [AutomaticRetry(OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(AnalyzerJobResource, 300)]
    public async Task CaptureClosingLineSnapshotsAsync(int lookaheadMinutes = 15)
    {
        var normalizedLookaheadMinutes = Math.Clamp(lookaheadMinutes, 1, 60);
        var nowUtc = DateTime.UtcNow;
        var windowEndUtc = nowUtc.AddMinutes(normalizedLookaheadMinutes);

        try
        {
            var candidatePredictions = await _dbContext.Predictions
                .AsNoTracking()
                .Where(prediction => prediction.IsCurrentRevision && prediction.WasPublished)
                .Where(prediction =>
                    prediction.MatchDateTime.HasValue &&
                    prediction.MatchDateTime.Value >= nowUtc &&
                    prediction.MatchDateTime.Value <= windowEndUtc)
                .OrderBy(prediction => prediction.MatchDateTime)
                .ToListAsync();

            if (candidatePredictions.Count == 0)
            {
                return;
            }

            var predictionIds = candidatePredictions.Select(prediction => prediction.Id).ToList();
            var existingClosePredictionIds = await _dbContext.PredictionOddsSnapshots
                .AsNoTracking()
                .Where(snapshot =>
                    predictionIds.Contains(snapshot.PredictionId) &&
                    snapshot.SnapshotKind == PredictionOddsSnapshotKind.Close)
                .Select(snapshot => snapshot.PredictionId)
                .Distinct()
                .ToListAsync();

            var pendingPredictions = candidatePredictions
                .Where(prediction => !existingClosePredictionIds.Contains(prediction.Id))
                .ToList();

            if (pendingPredictions.Count == 0)
            {
                return;
            }

            IReadOnlyList<SourceMarketFixture> sourceFixtures = [];
            try
            {
                sourceFixtures = await _sourceMarketPricingService.GetTodaySourceMarketFixturesAsync();
            }
            catch (Exception pricingEx)
            {
                _logger.LogWarning(pricingEx, "Failed to load live source pricing for closing-line snapshots. Close snapshots will fall back to derived pricing when possible.");
            }

            var targetDates = pendingPredictions
                .Select(prediction => prediction.MatchLocalDate)
                .Distinct()
                .ToList();

            var matchDatas = await _dbContext.MatchDatas
                .AsNoTracking()
                .Where(match => match.MatchLocalDate.HasValue && targetDates.Contains(match.MatchLocalDate.Value))
                .ToListAsync();

            var createdCount = await SavePredictionOddsSnapshotsAsync(pendingPredictions, matchDatas, sourceFixtures, PredictionOddsSnapshotKind.Close);

            if (createdCount > 0)
            {
                await LogScrapingStatus(
                    ClosingLineSnapshotEventName,
                    "Success",
                    $"✅ Captured {createdCount} closing-line snapshot(s) in the final {normalizedLookaheadMinutes}-minute window.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ An error occurred while capturing closing-line snapshots.");
            await LogScrapingStatus(ClosingLineSnapshotEventName, "Failed", $"Closing-line snapshot error: {ex.Message}");
            throw;
        }
    }
    [AutomaticRetry(OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(AnalyzerJobResource, 3600)]
    public async Task RunDailyAnalysisAsync()
    {
        _logger.LogInformation("Starting daily analysis process...");

        try
        {
            await BackfillStoredPredictionTimesAsync();
            _logger.LogInformation("✅ Stored prediction time backfill completed.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Stored prediction time backfill failed, continuing with analytics rebuild.");
        }

        try
        {
            await BackfillCanonicalFixtureFieldsAsync();
            _logger.LogInformation("✅ Canonical fixture field backfill completed.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Canonical fixture field backfill failed, continuing with analytics rebuild.");
        }

        try
        {
            await BackfillDecisionProvenanceAsync();
            _logger.LogInformation("✅ Decision provenance backfill completed.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Decision provenance backfill failed, continuing with analytics rebuild.");
        }

        // ── Step 1: Learning-loop retraining (correction → calibration → thresholds) ──
        await _learningLoopService.RebuildAllProfilesAsync();

        try
        {
            var sourceQualityRebuild = await RebuildSourceQualityProfilesAsync();
            if (sourceQualityRebuild.Completed)
            {
                _logger.LogInformation(
                    "✅ Source quality rebuild completed with {ProfileCount} profile(s).",
                    sourceQualityRebuild.ProfileCount);
                await LogScrapingStatus(
                    SourceQualityEventName,
                    "Success",
                    $"✅ Source quality profiles rebuilt successfully ({sourceQualityRebuild.ProfileCount} profile(s)).");
            }
            else
            {
                _logger.LogWarning(
                    "⚠️ Source quality rebuild skipped: {Reason}",
                    sourceQualityRebuild.StatusMessage);
                await LogScrapingStatus(
                    SourceQualityEventName,
                    "Failed",
                    $"Source quality rebuild skipped: {sourceQualityRebuild.StatusMessage}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Source quality rebuild failed, continuing with regression predictions.");
            await LogScrapingStatus(SourceQualityEventName, "Failed", $"Source quality rebuild error: {ex.Message}");
        }

        // ── Step 2: Download fresh data and generate regression predictions ──
        try
        {
            List<MatchData> scraped;

            // Try to download fresh Excel with the latest odds
            try
            {
                await _webScraperService.ScrapeMatchDataAsync();
                scraped = _excelExtract.ExtractMatchDatasetFromFile().ToList();
                _logger.LogInformation("✅ Fresh Excel downloaded. Extracted {Count} matches for regression.", scraped.Count);
            }
            catch (Exception dlEx)
            {
                _logger.LogWarning(dlEx, "⚠️ Fresh Excel download failed. Falling back to database data.");
                var todayDate = DateTimeProvider.GetLocalDate();
                scraped = await _dbContext.MatchDatas
                    .Where(match => match.MatchLocalDate == todayDate)
                    .ToListAsync();
            }

            if (scraped.Count == 0)
            {
                _logger.LogWarning("⚠️ No match data available for regression predictions. Skipping.");
            }
            else
            {
                var regressionPredictions = _regressionPredictorService.GeneratePredictions(scraped);
                await SaveRegressionPredictions(regressionPredictions);
                _logger.LogInformation("✅ Regression-based predictions saved ({Count} matches, {PredCount} predictions).", scraped.Count, regressionPredictions.Count());
            }

            await LogScrapingStatus(DailyAnalysisEventName, "Success", "✅ Daily analysis completed successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ An error occurred during regression prediction generation.");
            await LogScrapingStatus(DailyAnalysisEventName, "Failed", $"Daily Analysis Error: {ex.Message}");
            throw;
        }
    }
    private async Task LogScrapingStatus(string eventName, string status, string message)
    {
        try
        {
            var log = new ScrapingLog
            {
                EventName = string.IsNullOrWhiteSpace(eventName) ? "general" : eventName.Trim().ToLowerInvariant(),
                Timestamp = DateTime.UtcNow,
                Status = status,
                Message = message
            };
            await _dbContext.ScrapingLogs.AddAsync(log);
            await _dbContext.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write scraping log.");
        }
    }
    [AutomaticRetry(OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(AnalyzerJobResource, 1800)]
    public async Task CleanupOldPredictionsAndMatchDataAsync()
    {
        var cutoffDate = DateTimeProvider.GetLocalTime().AddDays(-90).Date;
        var cutoffDateOnly = DateOnly.FromDateTime(cutoffDate);
        var createdAtCutoffUtc = DateTime.SpecifyKind(cutoffDate, DateTimeKind.Utc);

        // Compare CreatedAt directly (no .Date) so the filter stays sargable in SQL.
        await _dbContext.Predictions
            .Where(p => p.CreatedAt < createdAtCutoffUtc)
            .ExecuteDeleteAsync();

        await _dbContext.ForecastObservations
            .Where(f => f.CreatedAt < createdAtCutoffUtc)
            .ExecuteDeleteAsync();

        // Delete old match data in the database via the indexed MatchLocalDate column.
        await _dbContext.MatchDatas
            .Where(m => m.MatchLocalDate != null && m.MatchLocalDate < cutoffDateOnly)
            .ExecuteDeleteAsync();

        // Legacy rows without a canonical MatchLocalDate only store a date string;
        // those few are filtered in memory.
        var legacyMatchData = await _dbContext.MatchDatas
            .Where(m => m.MatchLocalDate == null)
            .ToListAsync();
        var oldLegacyMatchData = legacyMatchData
            .Where(m => DateTime.TryParse(m.Date, out var d) && d.Date < cutoffDate)
            .ToList();

        if (oldLegacyMatchData.Count > 0)
        {
            _dbContext.MatchDatas.RemoveRange(oldLegacyMatchData);
            await _dbContext.SaveChangesAsync();
        }

        // Cleanup old AiScore match scores
        var cutoffUtc = DateTime.SpecifyKind(cutoffDate, DateTimeKind.Utc);
        await _dbContext.AiScoreMatchScores
            .Where(s => s.MatchTime < cutoffUtc)
            .ExecuteDeleteAsync();
    
        // Cleanup old MatchScores — retain 90 days for calibration and regression.
        var scoreCutoffDate = DateTimeProvider.GetLocalTime().AddDays(-90).Date;
        var scoreCutoffUtc = DateTime.SpecifyKind(scoreCutoffDate, DateTimeKind.Utc);
        await _dbContext.MatchScores
            .Where(s => s.MatchTime < scoreCutoffUtc)
            .ExecuteDeleteAsync();

        // Cleanup scraping logs older than 2 days
        var logCutoff = DateTimeProvider.GetLocalTime().AddDays(-2);
        var deletedLogs = await _dbContext.ScrapingLogs
            .Where(l => l.Timestamp < logCutoff)
            .ExecuteDeleteAsync();

        if (deletedLogs > 0)
        {
            _logger.LogInformation("🧹 Deleted {Count} scraping logs older than 2 days.", deletedLogs);
        }
    }
}
