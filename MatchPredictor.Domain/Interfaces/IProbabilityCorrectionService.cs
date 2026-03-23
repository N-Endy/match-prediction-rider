using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IProbabilityCorrectionService
{
    double ApplyCorrection(PredictionMarket market, double rawProbability);
    Task RebuildProfilesAsync();
}
