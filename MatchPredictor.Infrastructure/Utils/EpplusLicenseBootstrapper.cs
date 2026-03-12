using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OfficeOpenXml;

namespace MatchPredictor.Infrastructure.Utils;

public static class EpplusLicenseBootstrapper
{
    private static readonly object Sync = new();
    private static bool _initialized;
    private static string? _licenseSource;

    public static void EnsureInitialized(IConfiguration configuration, ILogger? logger = null)
    {
        if (_initialized)
            return;

        lock (Sync)
        {
            if (_initialized)
                return;

            var (configuredLicense, licenseSource) = ResolveConfiguredLicense(configuration);
            if (string.IsNullOrWhiteSpace(configuredLicense))
            {
                if (IsDevelopmentEnvironment())
                {
                    configuredLicense = "NonCommercialPersonal:MatchPredictor Development";
                    licenseSource = "development fallback";
                    logger?.LogWarning(
                        "EPPlus license was not configured. Falling back to a development-only noncommercial license. " +
                        "Set 'EPPlus:ExcelPackage:License' explicitly for shared or production environments.");
                }
                else
                {
                    throw new InvalidOperationException(
                        "EPPlus license is not configured. Set one of these configuration values to a real license: " +
                        "'EPPlus:ExcelPackage:License', 'EPPlus__ExcelPackage__License', 'EPPlusLicense', or 'EPPlusLicenseContext'. " +
                        "Allowed formats are 'Commercial:<license-key>', 'NonCommercialPersonal:<your-name>', or " +
                        "'NonCommercialOrganization:<organization-name>'.");
                }
            }

            ApplyLicense(configuredLicense);
            _licenseSource = licenseSource;
            _initialized = true;
            logger?.LogInformation("EPPlus license configured from {LicenseSource}.", _licenseSource);
        }
    }

    private static (string? value, string? source) ResolveConfiguredLicense(IConfiguration configuration)
    {
        var candidates = new (string? Value, string Source)[]
        {
            (configuration["EPPlus:ExcelPackage:License"], "configuration:EPPlus:ExcelPackage:License"),
            (configuration["EPPlus:ExcelPackage.License"], "configuration:EPPlus:ExcelPackage.License"),
            (Environment.GetEnvironmentVariable("EPPlus__ExcelPackage__License"), "env:EPPlus__ExcelPackage__License"),
            (Environment.GetEnvironmentVariable("EPPLUS__EXCELPACKAGE__LICENSE"), "env:EPPLUS__EXCELPACKAGE__LICENSE"),
            (Environment.GetEnvironmentVariable("EPPlus:ExcelPackage:License"), "env:EPPlus:ExcelPackage:License"),
            (Environment.GetEnvironmentVariable("EPPlusLicense"), "env:EPPlusLicense"),
            (Environment.GetEnvironmentVariable("EPPLUSLICENSE"), "env:EPPLUSLICENSE"),
            (Environment.GetEnvironmentVariable("EPPlusLicenseContext"), "env:EPPlusLicenseContext"),
            (Environment.GetEnvironmentVariable("EPPLUSLICENSECONTEXT"), "env:EPPLUSLICENSECONTEXT")
        };

        return candidates
            .Select(candidate => (Value: candidate.Value?.Trim(), candidate.Source))
            .FirstOrDefault(candidate => IsUsableLicenseValue(candidate.Value));
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
