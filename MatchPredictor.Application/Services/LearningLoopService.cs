using MatchPredictor.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatchPredictor.Application.Services;

/// <summary>
/// Nightly learning-loop orchestration, extracted from AnalyzerService.
/// Rebuild order matters: blend weights shape the raw probabilities, the
/// meta-model corrects raw, and calibration is trained on corrected values,
/// so the rebuilds run base-first.
/// </summary>
public class LearningLoopService : ILearningLoopService
{
    private readonly ICalibrationService _calibrationService;
    private readonly IProbabilityCorrectionService _probabilityCorrectionService;
    private readonly IThresholdTuningService _thresholdTuningService;
    private readonly IBlendWeightTuningService? _blendWeightTuningService;
    private readonly IMatchPredictorDbContext _dbContext;
    private readonly ILogger<LearningLoopService> _logger;

    public LearningLoopService(
        ICalibrationService calibrationService,
        IProbabilityCorrectionService probabilityCorrectionService,
        IThresholdTuningService thresholdTuningService,
        IMatchPredictorDbContext dbContext,
        ILogger<LearningLoopService> logger,
        IBlendWeightTuningService? blendWeightTuningService = null)
    {
        _calibrationService = calibrationService;
        _probabilityCorrectionService = probabilityCorrectionService;
        _thresholdTuningService = thresholdTuningService;
        _blendWeightTuningService = blendWeightTuningService;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task RebuildAllProfilesAsync()
    {
        if (_blendWeightTuningService is not null)
        {
            try
            {
                _logger.LogInformation("Starting blend weight rebuild...");
                await _blendWeightTuningService.RebuildProfilesAsync();
                var blendProfileCount = await _dbContext.BlendWeightProfiles.CountAsync();
                _logger.LogInformation("✅ Blend weight rebuild completed ({Count} profiles).", blendProfileCount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Blend weight rebuild failed, continuing with correction rebuild.");
            }
        }

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
