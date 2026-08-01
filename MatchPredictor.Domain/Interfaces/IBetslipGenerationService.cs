namespace MatchPredictor.Domain.Interfaces;

public interface IBetslipGenerationService
{
    Task GenerateDailyBetslipsAsync(string? runLabel = null);
}
