using System.Text.RegularExpressions;

namespace MatchPredictor.Web.Helpers;

public static partial class BetslipDisplayHelper
{
    /// <summary>
    /// Strips currency payout segments from stored tier labels, e.g.
    /// "Daily (30-100x · ₦3k-₦10k @ ₦100)" → "Daily (30-100x)".
    /// </summary>
    public static string SanitizeTierLabel(string? tierLabel)
    {
        if (string.IsNullOrWhiteSpace(tierLabel))
        {
            return string.Empty;
        }

        var sanitized = CurrencyPayoutSegmentRegex().Replace(tierLabel, string.Empty);
        sanitized = NairaSymbolRegex().Replace(sanitized, string.Empty);
        return sanitized.Trim();
    }

    [GeneratedRegex(@"\s*[·•]\s*₦[^)]*", RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyPayoutSegmentRegex();

    [GeneratedRegex(@"₦[0-9.,]*[kKmM]?", RegexOptions.CultureInvariant)]
    private static partial Regex NairaSymbolRegex();
}
