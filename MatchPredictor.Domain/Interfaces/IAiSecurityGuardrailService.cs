using MatchPredictor.Domain.Models;

namespace MatchPredictor.Domain.Interfaces;

public interface IAiSecurityGuardrailService
{
    SecurityGuardrailResult ValidateInboundPrompt(string userPrompt);
    string SanitizeOutboundContent(string content);
    AiChatResponse SanitizeOutboundResponse(AiChatResponse response);
    string GetHardenedSystemInstruction();
}
