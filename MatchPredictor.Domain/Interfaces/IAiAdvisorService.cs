namespace MatchPredictor.Domain.Interfaces;

public interface IAiAdvisorService
{
    Task<string> GetAdviceAsync(string userPrompt, CancellationToken ct = default);
}
