using MatchPredictor.Domain.Models;

namespace MatchPredictor.Application.Helpers;

public sealed record PayoutBandSpec
{
    public int SlipNumber { get; init; }
    public string Title { get; init; } = string.Empty;
    public string TierLabel { get; init; } = string.Empty;
    public string BandKey { get; init; } = string.Empty;
    public double MinOdds { get; init; }
    public double MaxOdds { get; init; }
    public double FallbackMinOdds { get; init; }
    public double FallbackMaxOdds { get; init; }
    public int MaxPicks { get; init; }

    /// <summary>
    /// When true, prefer higher individual odds (Big/Mega). When false, prefer shorter priced legs (Small/Medium/Daily).
    /// </summary>
    public bool PreferHigherSingles { get; init; }

    public bool IsMega => string.Equals(BandKey, "mega", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Packs weekend/weekday ladder slips by combined-odds payout bands rather than leg count.
/// </summary>
public static class WeekendPayoutSlipComposer
{
    public static IReadOnlyList<PayoutBandSpec> BuildWeekendPlan(BetslipSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var specs = new List<PayoutBandSpec>();
        var slipNumber = 1;

        AddBandCopies(
            specs,
            ref slipNumber,
            count: Math.Max(0, settings.WeekendSmallSlipCount),
            bandKey: "small",
            titlePrefix: "Small Acca",
            settings.SmallMinOdds,
            settings.SmallMaxOdds,
            settings.SmallFallbackMinOdds,
            settings.SmallFallbackMaxOdds,
            settings.SmallMaxPicks,
            preferHigherSingles: false);

        AddBandCopies(
            specs,
            ref slipNumber,
            count: Math.Max(0, settings.WeekendMediumSlipCount),
            bandKey: "medium",
            titlePrefix: "Medium Acca",
            settings.MediumMinOdds,
            settings.MediumMaxOdds,
            settings.MediumFallbackMinOdds,
            settings.MediumFallbackMaxOdds,
            settings.MediumMaxPicks,
            preferHigherSingles: false);

        AddBandCopies(
            specs,
            ref slipNumber,
            count: Math.Max(0, settings.WeekendBigSlipCount),
            bandKey: "big",
            titlePrefix: "Big Acca",
            settings.BigMinOdds,
            settings.BigMaxOdds,
            settings.BigFallbackMinOdds,
            settings.BigFallbackMaxOdds,
            settings.BigMaxPicks,
            preferHigherSingles: true);

        AddBandCopies(
            specs,
            ref slipNumber,
            count: Math.Max(0, settings.WeekendMegaSlipCount),
            bandKey: "mega",
            titlePrefix: "Mega Acca",
            settings.MegaMinOdds,
            settings.MegaMaxOdds,
            settings.MegaFallbackMinOdds,
            settings.MegaFallbackMaxOdds,
            Math.Min(settings.MegaMaxPicks, settings.MaxSelectionsPerSlip),
            preferHigherSingles: true);

        return specs;
    }

    public static IReadOnlyList<PayoutBandSpec> BuildWeekdayPlan(BetslipSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return
        [
            CreateSpec(
                slipNumber: 1,
                title: "Daily Acca",
                letter: null,
                bandKey: "daily",
                settings.SmallMinOdds,
                settings.SmallMaxOdds,
                settings.SmallFallbackMinOdds,
                settings.SmallFallbackMaxOdds,
                Math.Min(settings.DailyMaxPicks, settings.MaxSelectionsPerSlip),
                preferHigherSingles: false)
        ];
    }

    public static IReadOnlyList<ComposedBetslip> Compose(
        IReadOnlyList<BetslipComposerCandidate> candidates,
        IReadOnlyList<PayoutBandSpec> bands,
        int maxSlipsPerPrediction = 3,
        double maxSingleMarketShare = 0.4,
        double overlapPenalty = 0.08)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(bands);

        maxSlipsPerPrediction = Math.Max(1, maxSlipsPerPrediction);
        maxSingleMarketShare = Math.Clamp(maxSingleMarketShare, 0.1, 1.0);
        overlapPenalty = Math.Max(0, overlapPenalty);

        var usageCounts = new Dictionary<int, int>();
        var composed = new List<ComposedBetslip>(bands.Count);

        foreach (var band in bands)
        {
            var result = TryComposeBand(
                candidates,
                band,
                usageCounts,
                maxSlipsPerPrediction,
                maxSingleMarketShare,
                overlapPenalty,
                useFallback: false);

            var usedFallback = false;
            if (result.IsEmpty)
            {
                result = TryComposeBand(
                    candidates,
                    band with
                    {
                        MinOdds = band.FallbackMinOdds,
                        MaxOdds = band.FallbackMaxOdds
                    },
                    usageCounts,
                    maxSlipsPerPrediction,
                    maxSingleMarketShare,
                    overlapPenalty,
                    useFallback: true);
                usedFallback = !result.IsEmpty;
            }

            if (result.IsEmpty)
            {
                // Omit empty bands — do not pad with junk.
                continue;
            }

            foreach (var selection in result.Selections)
            {
                usageCounts[selection.PredictionId] = usageCounts.GetValueOrDefault(selection.PredictionId) + 1;
            }

            var notes = new List<string>();
            if (usedFallback)
            {
                notes.Add(
                    $"Widened payout range to {result.ActiveMinOdds:0.##}-{result.ActiveMaxOdds:0.##}x.");
            }

            composed.Add(new ComposedBetslip
            {
                SlipNumber = band.SlipNumber,
                Title = band.Title,
                TierLabel = band.TierLabel,
                TargetMinSelections = 1,
                TargetMaxSelections = band.MaxPicks,
                Selections = result.Selections,
                ShortfallNote = notes.Count > 0 ? string.Join(" ", notes) : null,
                TargetCombinedOdds = result.CombinedOdds,
                ActiveMinOdds = result.ActiveMinOdds,
                ActiveMaxOdds = result.ActiveMaxOdds,
                IsPayoutBand = true,
                IsMega = band.IsMega
            });
        }

        return composed;
    }

    private static BandPackResult TryComposeBand(
        IReadOnlyList<BetslipComposerCandidate> candidates,
        PayoutBandSpec band,
        IReadOnlyDictionary<int, int> usageCounts,
        int maxSlipsPerPrediction,
        double maxSingleMarketShare,
        double overlapPenalty,
        bool useFallback)
    {
        var minOdds = Math.Max(1.01, band.MinOdds);
        var maxOdds = Math.Max(minOdds, band.MaxOdds);
        var maxPicks = Math.Max(1, band.MaxPicks);

        var ordered = candidates
            .Where(c => c.DecimalOdds is > 1d)
            .Select(c => new
            {
                Candidate = c,
                Uses = usageCounts.GetValueOrDefault(c.PredictionId),
                Score = ResolvePackingScore(c) - overlapPenalty * usageCounts.GetValueOrDefault(c.PredictionId)
            })
            .Where(x => x.Uses < maxSlipsPerPrediction)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Candidate.ResearchScore ?? (double)x.Candidate.Confidence)
            .ThenByDescending(x => x.Candidate.Confidence)
            .ThenBy(x => band.PreferHigherSingles
                ? -(x.Candidate.DecimalOdds ?? 0d)
                : (x.Candidate.DecimalOdds ?? 0d))
            .ThenBy(x => x.Candidate.PredictionId)
            .Select(x => x.Candidate)
            .ToList();

        var selected = new List<BetslipComposerCandidate>();
        var usedFixtures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var marketCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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

            var marketKey = NormalizeMarketKey(candidate.PredictionCategory, candidate.Market);
            var marketCount = marketCounts.GetValueOrDefault(marketKey);
            var marketCap = Math.Max(1, (int)Math.Floor(maxPicks * maxSingleMarketShare));
            if (marketCount >= marketCap)
            {
                usedFixtures.Remove(fixtureKey);
                continue;
            }

            var nextProduct = product * candidate.DecimalOdds!.Value;
            if (nextProduct > maxOdds)
            {
                usedFixtures.Remove(fixtureKey);
                continue;
            }

            selected.Add(candidate);
            marketCounts[marketKey] = marketCount + 1;
            product = nextProduct;

            if (product >= minOdds)
            {
                break;
            }
        }

        if (selected.Count == 0 || product < minOdds || product > maxOdds)
        {
            return BandPackResult.Empty;
        }

        return new BandPackResult
        {
            Selections = selected
                .OrderBy(c => c.MatchDateTimeUtc ?? DateTime.MaxValue)
                .ThenBy(c => c.League)
                .ThenBy(c => c.HomeTeam)
                .ToList(),
            CombinedOdds = product,
            ActiveMinOdds = minOdds,
            ActiveMaxOdds = maxOdds,
            UsedFallback = useFallback
        };
    }

    private static void AddBandCopies(
        List<PayoutBandSpec> specs,
        ref int slipNumber,
        int count,
        string bandKey,
        string titlePrefix,
        double minOdds,
        double maxOdds,
        double fallbackMinOdds,
        double fallbackMaxOdds,
        int maxPicks,
        bool preferHigherSingles)
    {
        for (var i = 0; i < count; i++)
        {
            char? letter = count > 1 ? (char)('A' + i) : null;
            specs.Add(CreateSpec(
                slipNumber++,
                titlePrefix,
                letter,
                bandKey,
                minOdds,
                maxOdds,
                fallbackMinOdds,
                fallbackMaxOdds,
                maxPicks,
                preferHigherSingles));
        }
    }

    private static PayoutBandSpec CreateSpec(
        int slipNumber,
        string title,
        char? letter,
        string bandKey,
        double minOdds,
        double maxOdds,
        double fallbackMinOdds,
        double fallbackMaxOdds,
        int maxPicks,
        bool preferHigherSingles)
    {
        var displayTitle = letter is null ? title : $"{title} {letter}";
        var labelPrefix = bandKey switch
        {
            "small" => "Small",
            "medium" => "Medium",
            "big" => "Big",
            "mega" => "Mega",
            "daily" => "Daily",
            _ => bandKey
        };

        return new PayoutBandSpec
        {
            SlipNumber = slipNumber,
            Title = displayTitle,
            BandKey = bandKey,
            TierLabel =
                $"{labelPrefix} ({FormatOdds(minOdds)}-{FormatOdds(maxOdds)}x)",
            MinOdds = minOdds,
            MaxOdds = maxOdds,
            FallbackMinOdds = fallbackMinOdds,
            FallbackMaxOdds = fallbackMaxOdds,
            MaxPicks = Math.Max(1, maxPicks),
            PreferHigherSingles = preferHigherSingles
        };
    }

    private static string FormatOdds(double odds) =>
        odds >= 1000 ? odds.ToString("0") : odds.ToString("0.##");

    private static double ResolvePackingScore(BetslipComposerCandidate candidate) =>
        candidate.ResearchScore ?? (double)candidate.Confidence;

    private static string ResolveFixtureKey(BetslipComposerCandidate candidate) =>
        string.IsNullOrWhiteSpace(candidate.FixtureKey)
            ? $"{candidate.League}|{candidate.HomeTeam}|{candidate.AwayTeam}"
            : candidate.FixtureKey;

    private static string NormalizeMarketKey(string category, string market)
    {
        if (!string.IsNullOrWhiteSpace(category))
        {
            return category.Trim();
        }

        return string.IsNullOrWhiteSpace(market) ? "unknown" : market.Trim();
    }

    private sealed class BandPackResult
    {
        public static BandPackResult Empty { get; } = new();

        public IReadOnlyList<BetslipComposerCandidate> Selections { get; init; } = [];
        public double CombinedOdds { get; init; }
        public double ActiveMinOdds { get; init; }
        public double ActiveMaxOdds { get; init; }
        public bool UsedFallback { get; init; }
        public bool IsEmpty => Selections.Count == 0;
    }
}
