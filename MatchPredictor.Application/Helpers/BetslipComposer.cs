using MatchPredictor.Domain.Models;

namespace MatchPredictor.Application.Helpers;

public sealed class BetslipComposerCandidate
{
    public int PredictionId { get; init; }
    public string FixtureKey { get; init; } = string.Empty;
    public string League { get; init; } = string.Empty;
    public string HomeTeam { get; init; } = string.Empty;
    public string AwayTeam { get; init; } = string.Empty;
    public string Market { get; init; } = string.Empty;
    public string PredictedOutcome { get; init; } = string.Empty;
    public string PredictionCategory { get; init; } = string.Empty;
    public decimal Confidence { get; init; }
    public DateTime? MatchDateTimeUtc { get; init; }
    public double? DecimalOdds { get; init; }
    public string? AiNote { get; init; }
    public double? ResearchScore { get; init; }
}

public sealed class BetslipTierSpec
{
    public int SlipNumber { get; init; }
    public string Title { get; init; } = string.Empty;
    public string TierLabel { get; init; } = string.Empty;
    public int MinSelections { get; init; }
    public int MaxSelections { get; init; }
}

public sealed class ComposedBetslip
{
    public int SlipNumber { get; init; }
    public string Title { get; init; } = string.Empty;
    public string TierLabel { get; init; } = string.Empty;
    public int TargetMinSelections { get; init; }
    public int TargetMaxSelections { get; init; }
    public IReadOnlyList<BetslipComposerCandidate> Selections { get; init; } = [];
    public string? ShortfallNote { get; init; }
    public string? AiSummary { get; init; }
    public double? TargetCombinedOdds { get; init; }
    public bool IsBanker { get; init; }
    public bool IsPayoutBand { get; init; }
    public bool IsMega { get; init; }
    public double? ActiveMinOdds { get; init; }
    public double? ActiveMaxOdds { get; init; }
}

public static class BetslipComposer
{
    /// <summary>Legacy leg-count weekend plan — prefer <see cref="WeekendPayoutSlipComposer"/>.</summary>
    [Obsolete("Use WeekendPayoutSlipComposer.BuildWeekendPlan for payout-band slips.")]
    public static IReadOnlyList<BetslipTierSpec> WeekendTierPlan(int maxSelectionsPerSlip = 50) =>
        WeekendPayoutSlipComposer.BuildWeekendPlan(new BetslipSettings { MaxSelectionsPerSlip = maxSelectionsPerSlip })
            .Select(b => new BetslipTierSpec
            {
                SlipNumber = b.SlipNumber,
                Title = b.Title,
                TierLabel = b.TierLabel,
                MinSelections = 1,
                MaxSelections = b.MaxPicks
            })
            .ToList();

    /// <summary>Legacy weekday plan — prefer <see cref="WeekendPayoutSlipComposer"/>.</summary>
    [Obsolete("Use WeekendPayoutSlipComposer.BuildWeekdayPlan for payout-band slips.")]
    public static IReadOnlyList<BetslipTierSpec> WeekdayTierPlan(int maxSelectionsPerSlip = 50) =>
        WeekendPayoutSlipComposer.BuildWeekdayPlan(new BetslipSettings { MaxSelectionsPerSlip = maxSelectionsPerSlip })
            .Select(b => new BetslipTierSpec
            {
                SlipNumber = b.SlipNumber,
                Title = b.Title,
                TierLabel = b.TierLabel,
                MinSelections = 1,
                MaxSelections = b.MaxPicks
            })
            .ToList();

    public static IReadOnlyList<ComposedBetslip> Compose(
        IReadOnlyList<BetslipComposerCandidate> candidates,
        IReadOnlyList<BetslipTierSpec> tiers,
        int maxSlipsPerPrediction = 3,
        double maxSingleMarketShare = 0.4,
        double overlapPenalty = 0.08,
        double overProvisionFactor = 1.2)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(tiers);

        maxSlipsPerPrediction = Math.Max(1, maxSlipsPerPrediction);
        maxSingleMarketShare = Math.Clamp(maxSingleMarketShare, 0.1, 1.0);
        overlapPenalty = Math.Max(0, overlapPenalty);
        overProvisionFactor = Math.Max(1.0, overProvisionFactor);

        var usageCounts = new Dictionary<int, int>();
        var composed = new List<ComposedBetslip>(tiers.Count);

        foreach (var tier in tiers)
        {
            var targetMax = Math.Max(1, tier.MaxSelections);
            var fillTarget = Math.Min(
                targetMax,
                (int)Math.Ceiling(targetMax * overProvisionFactor));

            var selected = new List<BetslipComposerCandidate>();
            var usedFixtures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var marketCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var ordered = candidates
                .Select(c => new
                {
                    Candidate = c,
                    Uses = usageCounts.GetValueOrDefault(c.PredictionId),
                    Score = (double)c.Confidence - overlapPenalty * usageCounts.GetValueOrDefault(c.PredictionId)
                })
                .Where(x => x.Uses < maxSlipsPerPrediction)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Candidate.Confidence)
                .ThenBy(x => x.Candidate.PredictionId)
                .Select(x => x.Candidate)
                .ToList();

            foreach (var candidate in ordered)
            {
                if (selected.Count >= fillTarget)
                {
                    break;
                }

                var fixtureKey = string.IsNullOrWhiteSpace(candidate.FixtureKey)
                    ? $"{candidate.League}|{candidate.HomeTeam}|{candidate.AwayTeam}"
                    : candidate.FixtureKey;

                if (!usedFixtures.Add(fixtureKey))
                {
                    continue;
                }

                var marketKey = NormalizeMarketKey(candidate.PredictionCategory, candidate.Market);
                var marketCount = marketCounts.GetValueOrDefault(marketKey);
                var marketCap = Math.Max(1, (int)Math.Floor(fillTarget * maxSingleMarketShare));
                if (marketCount >= marketCap)
                {
                    usedFixtures.Remove(fixtureKey);
                    continue;
                }

                selected.Add(candidate);
                marketCounts[marketKey] = marketCount + 1;
                usageCounts[candidate.PredictionId] = usageCounts.GetValueOrDefault(candidate.PredictionId) + 1;
            }

            // Trim over-provisioned extras down to the tier max while keeping highest confidence.
            if (selected.Count > targetMax)
            {
                var keep = selected
                    .OrderByDescending(c => c.Confidence)
                    .ThenBy(c => c.PredictionId)
                    .Take(targetMax)
                    .ToHashSet();

                foreach (var dropped in selected.Where(c => !keep.Contains(c)))
                {
                    usageCounts[dropped.PredictionId] = Math.Max(0, usageCounts.GetValueOrDefault(dropped.PredictionId) - 1);
                }

                selected = selected.Where(keep.Contains).ToList();
            }

            string? shortfall = null;
            if (selected.Count < tier.MinSelections)
            {
                shortfall = $"Only {selected.Count} bookable picks available (target {tier.MinSelections}-{tier.MaxSelections}).";
            }

            composed.Add(new ComposedBetslip
            {
                SlipNumber = tier.SlipNumber,
                Title = tier.Title,
                TierLabel = tier.TierLabel,
                TargetMinSelections = tier.MinSelections,
                TargetMaxSelections = tier.MaxSelections,
                Selections = selected
                    .OrderBy(c => c.MatchDateTimeUtc ?? DateTime.MaxValue)
                    .ThenBy(c => c.League)
                    .ThenBy(c => c.HomeTeam)
                    .ToList(),
                ShortfallNote = shortfall
            });
        }

        return composed;
    }

    private static string NormalizeMarketKey(string category, string market)
    {
        if (!string.IsNullOrWhiteSpace(category))
        {
            return category.Trim();
        }

        return string.IsNullOrWhiteSpace(market) ? "Unknown" : market.Trim();
    }
}
