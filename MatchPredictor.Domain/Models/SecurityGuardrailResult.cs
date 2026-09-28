namespace MatchPredictor.Domain.Models;

public class SecurityGuardrailResult
{
    public bool IsAllowed { get; set; } = true;
    public string? Reason { get; set; }
    public string? RefusalCategory { get; set; }
    public AiChatResponse? RefusalResponse { get; set; }

    public static SecurityGuardrailResult Allowed() => new() { IsAllowed = true };

    public static SecurityGuardrailResult Refusal(string category, string reason, AiChatResponse refusalResponse) =>
        new()
        {
            IsAllowed = false,
            RefusalCategory = category,
            Reason = reason,
            RefusalResponse = refusalResponse
        };
}
