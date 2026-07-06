using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface ICalibrationService
{
    double Calibrate(PredictionMarket market, double rawProbability, string? league = null);
    CalibrationDecision CalibrateWithDecision(PredictionMarket market, double rawProbability, string? league = null);
    Task RebuildProfilesAsync();
}
