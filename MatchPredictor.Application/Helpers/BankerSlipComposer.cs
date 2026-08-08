namespace MatchPredictor.Application.Helpers;

public sealed record BankerCompositionResult
{
    public IReadOnlyList<BetslipComposerCandidate> Selections { get; init; } = [];
    public double CombinedOdds { get; init; }
    public bool UsedFallbackRange { get; init; }
    public double ActiveMinOdds { get; init; }
    public double ActiveMaxOdds { get; init; }

    public bool IsEmpty => Selections.Count == 0;
}

public static class BankerSlipComposer
{
    public static BankerCompositionResult Compose(
        IReadOnlyList<BetslipComposerCandidate> candidates,
        double minOdds,
        double maxOdds,
        double fallbackMinOdds,
        double fallbackMaxOdds,
        int maxPicks = 8)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        maxPicks = Math.Max(1, maxPicks);
        minOdds = Math.Max(1.01, minOdds);
        maxOdds = Math.Max(minOdds, maxOdds);
        fallbackMinOdds = Math.Max(1.01, fallbackMinOdds);
        fallbackMaxOdds = Math.Max(fallbackMinOdds, fallbackMaxOdds);

        var priced = candidates
            .Where(c => c.DecimalOdds is > 1d)
            .ToList();

        var primary = TryCompose(priced, minOdds, maxOdds, maxPicks);
        if (!primary.IsEmpty)
        {
            return primary with
            {
                UsedFallbackRange = false,
                ActiveMinOdds = minOdds,
                ActiveMaxOdds = maxOdds
            };
        }

        var fallback = TryCompose(priced, fallbackMinOdds, fallbackMaxOdds, maxPicks);
        if (!fallback.IsEmpty)
        {
            return fallback with
            {
                UsedFallbackRange = true,
                ActiveMinOdds = fallbackMinOdds,
                ActiveMaxOdds = fallbackMaxOdds
            };
        }

        return new BankerCompositionResult
        {
            UsedFallbackRange = true,
            ActiveMinOdds = fallbackMinOdds,
            ActiveMaxOdds = fallbackMaxOdds
        };
    }

    public static double CalculateCombinedOdds(IEnumerable<BetslipComposerCandidate> selections)
    {
        var product = 1d;
        var count = 0;
        foreach (var selection in selections)
        {
            if (selection.DecimalOdds is not > 1d)
            {
                return 0d;
            }

            product *= selection.DecimalOdds.Value;
            count++;
        }

        return count > 0 ? product : 0d;
    }

    public static bool IsWithinOddsRange(double combinedOdds, double minOdds, double maxOdds) =>
        combinedOdds >= minOdds && combinedOdds <= maxOdds;

    private static BankerCompositionResult TryCompose(
        IReadOnlyList<BetslipComposerCandidate> priced,
        double minOdds,
        double maxOdds,
        int maxPicks)
    {
        var ordered = priced
            .OrderByDescending(c => c.Confidence)
            .ThenBy(c => c.DecimalOdds)
            .ThenBy(c => c.PredictionId)
            .ToList();

        var selected = new List<BetslipComposerCandidate>();
        var usedFixtures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var product = 1d;

        foreach (var candidate in ordered)
        {
            if (selected.Count >= maxPicks)
            {
                break;
            }

            var fixtureKey = ResolveFixtureKey(candidate);
            if (!usedFixtures.Add(fixtureKey))
            {
                continue;
            }

            var nextProduct = product * candidate.DecimalOdds!.Value;
            if (nextProduct > maxOdds)
            {
                usedFixtures.Remove(fixtureKey);
                continue;
            }

            selected.Add(candidate);
            product = nextProduct;

            // Once we are inside the target band, stop — lower product is safer for a banker.
            if (product >= minOdds)
            {
                break;
            }
        }

        if (selected.Count == 0 || product < minOdds || product > maxOdds)
        {
            return new BankerCompositionResult();
        }

        return new BankerCompositionResult
        {
            Selections = selected
                .OrderBy(c => c.MatchDateTimeUtc ?? DateTime.MaxValue)
                .ThenBy(c => c.League)
                .ThenBy(c => c.HomeTeam)
                .ToList(),
            CombinedOdds = product
        };
    }

    private static string ResolveFixtureKey(BetslipComposerCandidate candidate) =>
        string.IsNullOrWhiteSpace(candidate.FixtureKey)
            ? $"{candidate.League}|{candidate.HomeTeam}|{candidate.AwayTeam}"
            : candidate.FixtureKey;
}
