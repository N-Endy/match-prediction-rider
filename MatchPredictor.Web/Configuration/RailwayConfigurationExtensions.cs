using MatchPredictor.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;

namespace MatchPredictor.Web.Configuration;

/// <summary>
/// Extension methods for configuring Railway and PaaS environment variables.
/// </summary>
public static class RailwayConfigurationExtensions
{
    /// <summary>
    /// Inspects environment variables and existing configuration, normalizes common Railway aliases
    /// (e.g. ENABLE_BOOKING, SPORTYBET_ENABLE_BOOKING, ADSENSE_REVIEW_SAFE, DATABASE_URL),
    /// and adds them to the configuration pipeline with highest precedence.
    /// </summary>
    public static IConfigurationBuilder AddRailwayEnvironmentAliases(this IConfigurationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Build temporary config from sources added so far (appsettings.json, appsettings.{Env}.json, and standard env vars)
        var interimConfig = builder.Build();
        var normalizedMap = RailwayConfigurationNormalizer.BuildNormalizedMap(interimConfig);

        if (normalizedMap.Count > 0)
        {
            builder.AddInMemoryCollection(normalizedMap);
        }

        return builder;
    }
}
