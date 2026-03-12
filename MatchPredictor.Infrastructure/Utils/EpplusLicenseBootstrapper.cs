using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OfficeOpenXml;

namespace MatchPredictor.Infrastructure.Utils;

public static class EpplusLicenseBootstrapper
{
    private static readonly object Sync = new();
    private static bool _initialized;

    public static void EnsureInitialized(IConfiguration configuration, ILogger? logger = null)
    {
        if (_initialized)
            return;

        lock (Sync)
        {
            if (_initialized)
                return;

            var configuredLicense = ResolveConfiguredLicense(configuration);
            if (string.IsNullOrWhiteSpace(configuredLicense))
            {
                if (IsDevelopmentEnvironment())
                {
                    configuredLicense = "NonCommercialPersonal:MatchPredictor Development";
                    logger?.LogWarning(
                        "EPPlus license was not configured. Falling back to a development-only noncommercial license. " +
                        "Set 'EPPlus:ExcelPackage:License' explicitly for shared or production environments.");
                }
                else
                {
                    throw new InvalidOperationException(
                        "EPPlus license is not configured. Set 'EPPlus:ExcelPackage:License' to one of: " +
                        "'Commercial:<license-key>', 'NonCommercialPersonal:<your-name>', or " +
                        "'NonCommercialOrganization:<organization-name>'.");
                }
            }

            ApplyLicense(configuredLicense);
            _initialized = true;
        }
    }

    private static string? ResolveConfiguredLicense(IConfiguration configuration)
    {
        var candidates = new[]
        {
            configuration["EPPlus:ExcelPackage:License"],
            configuration["EPPlus:ExcelPackage.License"],
            Environment.GetEnvironmentVariable("EPPlusLicense"),
            Environment.GetEnvironmentVariable("EPPlusLicenseContext")
        };

        return candidates
            .Select(candidate => candidate?.Trim())
            .FirstOrDefault(IsUsableLicenseValue);
    }

    private static bool IsUsableLicenseValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (!value.Contains(':'))
            return false;

        if (value.Contains("Your Name", StringComparison.OrdinalIgnoreCase))
            return false;

        if (value.Contains("<Your", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static void ApplyLicense(string configuredLicense)
    {
        var separatorIndex = configuredLicense.IndexOf(':');
        if (separatorIndex <= 0 || separatorIndex == configuredLicense.Length - 1)
        {
            throw new InvalidOperationException(
                "Invalid EPPlus license format. Expected 'Commercial:<license-key>', " +
                "'NonCommercialPersonal:<your-name>', or 'NonCommercialOrganization:<organization-name>'.");
        }

        var licenseMode = configuredLicense[..separatorIndex].Trim();
        var licenseValue = configuredLicense[(separatorIndex + 1)..].Trim();

        switch (licenseMode.ToLowerInvariant())
        {
            case "commercial":
                ExcelPackage.License.SetCommercial(licenseValue);
                break;
            case "noncommercialpersonal":
                ExcelPackage.License.SetNonCommercialPersonal(licenseValue);
                break;
            case "noncommercialorganization":
                ExcelPackage.License.SetNonCommercialOrganization(licenseValue);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported EPPlus license mode '{licenseMode}'. " +
                    "Use Commercial, NonCommercialPersonal, or NonCommercialOrganization.");
        }
    }

    private static bool IsDevelopmentEnvironment()
    {
        var environmentName =
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ??
            Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        return string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);
    }
}
