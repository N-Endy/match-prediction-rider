using Microsoft.Extensions.Configuration;

namespace MatchPredictor.Infrastructure.Utils;

internal static class AiConfigurationHelper
{
    internal const string DefaultGroqModel = "llama-3.3-70b-versatile";

    internal static string? GetGroqApiKey(IConfiguration configuration)
    {
        return FirstNonEmpty(
            configuration["GroqApiKey"],
            configuration["GROQ_API_KEY"],
            Environment.GetEnvironmentVariable("GROQ_API_KEY"));
    }

    internal static string GetGroqModel(IConfiguration configuration)
    {
        return FirstNonEmpty(
                   configuration["GroqModel"],
                   configuration["GROQ_MODEL"],
                   Environment.GetEnvironmentVariable("GROQ_MODEL"))
               ?? DefaultGroqModel;
    }

    internal static bool IsMissingOrPlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return value.Contains("stored in user-secrets", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("set via environment variable", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}
