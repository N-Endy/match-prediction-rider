using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface ICalibrationService
{
    double Calibrate(PredictionMarket market, double rawProbability);
    CalibrationDecision CalibrateWithDecision(PredictionMarket market, double rawProbability);
    Task RebuildProfilesAsync();
}
