using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatchPredictor.Application.Services;

/// <summary>
/// Nightly learning-loop orchestration, extracted from AnalyzerService.
/// Rebuild order matters: the meta-model corrects raw probabilities, and
/// calibration is trained on corrected values, so correction rebuilds first.
/// </summary>
public class LearningLoopService : ILearningLoopService
{
    private readonly ICalibrationService _calibrationService;
    private readonly IProbabilityCorrectionService _probabilityCorrectionService;
    private readonly IThresholdTuningService _thresholdTuningService;
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<LearningLoopService> _logger;

    public LearningLoopService(
        ICalibrationService calibrationService,
        IProbabilityCorrectionService probabilityCorrectionService,
        IThresholdTuningService thresholdTuningService,
        ApplicationDbContext dbContext,
        ILogger<LearningLoopService> logger)
    {
        _calibrationService = calibrationService;
        _probabilityCorrectionService = probabilityCorrectionService;
        _thresholdTuningService = thresholdTuningService;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task RebuildAllProfilesAsync()
    {
        try
        {
            _logger.LogInformation("Starting probability correction rebuild...");
            await _probabilityCorrectionService.RebuildProfilesAsync();
            var correctionProfileCount = await _dbContext.MetaModelProfiles.CountAsync();
            _logger.LogInformation("✅ Probability correction rebuild completed ({Count} profiles).", correctionProfileCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Probability correction rebuild failed, continuing with calibration rebuild.");
        }

        try
        {
            _logger.LogInformation("Starting market calibration rebuild...");
            await _calibrationService.RebuildProfilesAsync();
            var calibrationProfileCount = await _dbContext.MarketCalibrationProfiles.CountAsync();
            _logger.LogInformation("✅ Calibration rebuild completed ({Count} profiles).", calibrationProfileCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Calibration rebuild failed, continuing with threshold tuning.");
        }

        try
        {
            _logger.LogInformation("Starting threshold tuning rebuild...");
            await _thresholdTuningService.RebuildProfilesAsync();
            var thresholdProfileCount = await _dbContext.ThresholdProfiles.CountAsync();
            _logger.LogInformation("✅ Threshold tuning rebuild completed ({Count} profiles).", thresholdProfileCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Threshold tuning rebuild failed.");
        }
    }
}
