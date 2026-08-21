namespace MatchPredictor.Application.Helpers;

public sealed record BankerCompositionResult
{
    public IReadOnlyList<BetslipComposerCandidate> Selections { get; init; } = [];
    public double CombinedOdds { get; init; }
    public bool UsedFallbackRange { get; init; }
    public double ActiveMinOdds { get; init; }
    public double ActiveMaxOdds { get; init; }

    /// <summary>
    /// Highest product found with &lt;= maxPicks legs that does not exceed the active max band.
    /// Useful when the composer returns empty (product stayed below the floor).
    /// </summary>
    public double BestAchievableProduct { get; init; }

    public bool IsEmpty => Selections.Count == 0;
}

public static class BankerSlipComposer
{
    private const int CandidateShortlistSize = 40;
    private const int MaxExploredNodes = 200_000;

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
            ActiveMaxOdds = fallbackMaxOdds,
            BestAchievableProduct = Math.Max(primary.BestAchievableProduct, fallback.BestAchievableProduct)
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
        var shortlist = BuildShortlist(priced);
        if (shortlist.Count == 0)
        {
            return new BankerCompositionResult();
        }

        // Odds-descending so the band is reachable in fewer legs; confidence breaks ties.
        var ordered = shortlist
            .OrderByDescending(c => c.DecimalOdds!.Value)
            .ThenByDescending(c => c.Confidence)
            .ThenBy(c => c.PredictionId)
            .ToList();

        var suffixMaxProduct = BuildSuffixMaxProducts(ordered);
        var bestValid = (Selections: (List<BetslipComposerCandidate>?)null, Product: 0d, AvgConfidence: 0d);
        var bestAchievable = 1d;
        var nodes = 0;

        Search(
            index: 0,
            product: 1d,
            selected: [],
            usedFixtures: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ordered,
            suffixMaxProduct,
            minOdds,
            maxOdds,
            maxPicks,
            ref bestValid,
            ref bestAchievable,
            ref nodes);

        if (bestValid.Selections is null || bestValid.Selections.Count == 0)
        {
            return new BankerCompositionResult
            {
                BestAchievableProduct = bestAchievable > 1d ? bestAchievable : 0d
            };
        }

        return new BankerCompositionResult
        {
            Selections = bestValid.Selections
                .OrderBy(c => c.MatchDateTimeUtc ?? DateTime.MaxValue)
                .ThenBy(c => c.League)
                .ThenBy(c => c.HomeTeam)
                .ToList(),
            CombinedOdds = bestValid.Product,
            BestAchievableProduct = Math.Max(bestAchievable, bestValid.Product)
        };
    }

    private static List<BetslipComposerCandidate> BuildShortlist(IReadOnlyList<BetslipComposerCandidate> priced)
    {
        // Highest confidence first, one market per fixture, then cap at top-N.
        return priced
            .OrderByDescending(c => c.Confidence)
            .ThenBy(c => c.DecimalOdds)
            .ThenBy(c => c.PredictionId)
            .GroupBy(ResolveFixtureKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(c => c.Confidence)
            .ThenBy(c => c.DecimalOdds)
            .ThenBy(c => c.PredictionId)
            .Take(CandidateShortlistSize)
            .ToList();
    }

    private static double[] BuildSuffixMaxProducts(IReadOnlyList<BetslipComposerCandidate> ordered)
    {
        // suffixMaxProduct[i] = product of odds from i..end (inclusive), or 1 if i == Count.
        var suffix = new double[ordered.Count + 1];
        suffix[ordered.Count] = 1d;
        for (var i = ordered.Count - 1; i >= 0; i--)
        {
            suffix[i] = suffix[i + 1] * ordered[i].DecimalOdds!.Value;
        }

        return suffix;
    }

    private static void Search(
        int index,
        double product,
        List<BetslipComposerCandidate> selected,
        HashSet<string> usedFixtures,
        IReadOnlyList<BetslipComposerCandidate> ordered,
        double[] suffixMaxProduct,
        double minOdds,
        double maxOdds,
        int maxPicks,
        ref (List<BetslipComposerCandidate>? Selections, double Product, double AvgConfidence) bestValid,
        ref double bestAchievable,
        ref int nodes)
    {
        if (nodes >= MaxExploredNodes)
        {
            return;
        }

        nodes++;

        if (product <= maxOdds)
        {
            bestAchievable = Math.Max(bestAchievable, product);
        }

        if (selected.Count > 0 && product >= minOdds && product <= maxOdds)
        {
            var avgConfidence = selected.Average(c => (double)c.Confidence);
            if (IsBetterCombination(
                    selected.Count,
                    avgConfidence,
                    product,
                    bestValid.Selections?.Count ?? int.MaxValue,
                    bestValid.AvgConfidence,
                    bestValid.Product))
            {
                bestValid = (selected.ToList(), product, avgConfidence);
            }

            // Inside the band: do not add more legs (fewer legs preferred).
            return;
        }

        if (selected.Count >= maxPicks || index >= ordered.Count)
        {
            return;
        }

        // Already have an L-leg solution in band; any continuation needs more legs and cannot improve.
        if (bestValid.Selections is not null &&
            selected.Count >= bestValid.Selections.Count &&
            product < minOdds)
        {
            return;
        }

        // Even multiplying by every remaining leg cannot reach the floor.
        if (product * suffixMaxProduct[index] < minOdds)
        {
            return;
        }

        for (var i = index; i < ordered.Count; i++)
        {
            if (nodes >= MaxExploredNodes)
            {
                return;
            }

            var candidate = ordered[i];
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

            // Remaining legs (after taking this one) cannot reach the floor.
            if (selected.Count + 1 < maxPicks &&
                nextProduct * suffixMaxProduct[i + 1] < minOdds &&
                nextProduct < minOdds)
            {
                usedFixtures.Remove(fixtureKey);
                continue;
            }

            selected.Add(candidate);
            Search(
                i + 1,
                nextProduct,
                selected,
                usedFixtures,
                ordered,
                suffixMaxProduct,
                minOdds,
                maxOdds,
                maxPicks,
                ref bestValid,
                ref bestAchievable,
                ref nodes);
            selected.RemoveAt(selected.Count - 1);
            usedFixtures.Remove(fixtureKey);
        }
    }

    private static bool IsBetterCombination(
        int legCount,
        double avgConfidence,
        double product,
        int bestLegCount,
        double bestAvgConfidence,
        double bestProduct)
    {
        if (legCount != bestLegCount)
        {
            return legCount < bestLegCount;
        }

        if (Math.Abs(avgConfidence - bestAvgConfidence) > 1e-9)
        {
            return avgConfidence > bestAvgConfidence;
        }

        return product < bestProduct || bestProduct <= 0d;
    }

    private static string ResolveFixtureKey(BetslipComposerCandidate candidate) =>
        string.IsNullOrWhiteSpace(candidate.FixtureKey)
            ? $"{candidate.League}|{candidate.HomeTeam}|{candidate.AwayTeam}"
            : candidate.FixtureKey;
}
