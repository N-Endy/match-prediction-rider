namespace MatchPredictor.Domain.Models;

public static class BetPricingMath
{
    /// <summary>Default fractional Kelly multiplier used across Analytics and Value Bets.</summary>
    public const double DefaultKellyFraction = 0.25;

    public static double? ConvertProbabilityToDecimalOdds(double? probability)
    {
        if (probability is null || probability <= 0d || probability > 1d)
        {
            return null;
        }

        return Math.Round(1d / probability.Value, 4);
    }

    public static double? ConvertDecimalOddsToProbability(double? decimalOdds)
    {
        if (decimalOdds is null || decimalOdds <= 1d)
        {
            return null;
        }

        return Math.Round(1d / decimalOdds.Value, 6);
    }

    public static bool MeetsMinimumEdge(
        double modelProbability,
        double marketProbability,
        double minimumEdge = 0.03) =>
        modelProbability - marketProbability >= minimumEdge;

    public static double? CalculateExpectedValuePercent(double? modelProbability, double? decimalOdds)
    {
        if (modelProbability is null || decimalOdds is null || modelProbability <= 0d || decimalOdds <= 1d)
        {
            return null;
        }

        return Math.Round((modelProbability.Value * decimalOdds.Value) - 1d, 6);
    }

    public static double? CalculateClosingLineValuePercent(double? publishOdds, double? closingOdds)
    {
        if (publishOdds is null || closingOdds is null || publishOdds <= 1d || closingOdds <= 1d)
        {
            return null;
        }

        return Math.Round((publishOdds.Value / closingOdds.Value) - 1d, 6);
    }

    /// <summary>
    /// Fractional Kelly stake as a share of bankroll (0–1).
    /// Matches Analytics: raw Kelly from model probability and decimal odds, then multiplied by <paramref name="kellyFraction"/>.
    /// </summary>
    public static double CalculateFractionalKellyStakeFraction(
        double modelProbability,
        double decimalOdds,
        double kellyFraction = DefaultKellyFraction)
    {
        if (decimalOdds <= 1d || kellyFraction <= 0d)
        {
            return 0d;
        }

        var probability = Math.Clamp(modelProbability, 0d, 1d);
        var b = decimalOdds - 1d;
        if (b <= 0d)
        {
            return 0d;
        }

        var rawFraction = ((b * probability) - (1d - probability)) / b;
        if (rawFraction <= 0d)
        {
            return 0d;
        }

        return Math.Clamp(rawFraction * kellyFraction, 0d, 1d);
    }

    /// <summary>
    /// Optimizes fractional Kelly stakes across concurrent bets within overlapping kickoff windows,
    /// enforcing aggregate window bankroll exposure limits and fixture exclusivity.
    /// </summary>
    public static IReadOnlyDictionary<string, SimultaneousKellyAllocation> OptimizeSimultaneousKellyStakes(
        IReadOnlyList<SimultaneousKellyCandidate> candidates,
        SimultaneousKellyOptions? options = null)
    {
        options ??= new SimultaneousKellyOptions();
        if (candidates is null || candidates.Count == 0)
        {
            return new Dictionary<string, SimultaneousKellyAllocation>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, SimultaneousKellyAllocation>(StringComparer.Ordinal);
        var ordered = candidates.OrderBy(c => c.KickoffUtc).ToList();
        var clusters = new List<List<SimultaneousKellyCandidate>>();
        var currentCluster = new List<SimultaneousKellyCandidate>();

        foreach (var candidate in ordered)
        {
            if (currentCluster.Count == 0)
            {
                currentCluster.Add(candidate);
            }
            else
            {
                var previousKickoff = currentCluster[^1].KickoffUtc;
                if (Math.Abs((candidate.KickoffUtc - previousKickoff).TotalMinutes) <= options.WindowToleranceMinutes)
                {
                    currentCluster.Add(candidate);
                }
                else
                {
                    clusters.Add(currentCluster);
                    currentCluster = [candidate];
                }
            }
        }

        if (currentCluster.Count > 0)
        {
            clusters.Add(currentCluster);
        }

        foreach (var cluster in clusters)
        {
            var activeCandidates = new List<SimultaneousKellyCandidate>();
            var excludedCandidates = new List<SimultaneousKellyCandidate>();

            if (options.EnforceFixtureExclusivity)
            {
                var fixtureGroups = cluster.GroupBy(c => c.FixtureKey, StringComparer.OrdinalIgnoreCase);
                foreach (var group in fixtureGroups)
                {
                    var bestPick = group
                        .OrderByDescending(c => c.ExpectedValuePercent)
                        .ThenByDescending(c => c.Edge)
                        .First();

                    activeCandidates.Add(bestPick);
                    foreach (var subordinate in group)
                    {
                        if (!ReferenceEquals(subordinate, bestPick))
                        {
                            excludedCandidates.Add(subordinate);
                        }
                    }
                }
            }
            else
            {
                activeCandidates.AddRange(cluster);
            }

            var standaloneStakes = new Dictionary<string, double>(StringComparer.Ordinal);
            var excessReturns = new Dictionary<string, double>(StringComparer.Ordinal);
            var variances = new Dictionary<string, double>(StringComparer.Ordinal);

            foreach (var c in activeCandidates)
            {
                var standalone = CalculateFractionalKellyStakeFraction(c.ModelProbability, c.DecimalOdds, options.KellyFraction);
                standaloneStakes[c.CandidateKey] = standalone;

                var b = Math.Max(0.0001, c.DecimalOdds - 1.0);
                var p = Math.Clamp(c.ModelProbability, 0.0, 1.0);
                var excessReturn = (b * p) - (1.0 - p);
                var variance = (p * (1.0 - p) * b * b) + ((1.0 - p) * 1.0);
                excessReturns[c.CandidateKey] = excessReturn;
                variances[c.CandidateKey] = Math.Max(0.001, variance);
            }

            var totalStandalone = standaloneStakes.Values.Sum();
            var wasCapped = totalStandalone > options.MaxWindowExposureFraction + 1e-6;
            var portfolioStakes = new Dictionary<string, double>(StringComparer.Ordinal);

            if (!wasCapped || activeCandidates.Count == 0)
            {
                foreach (var c in activeCandidates)
                {
                    portfolioStakes[c.CandidateKey] = standaloneStakes[c.CandidateKey];
                }
            }
            else
            {
                double low = 0.0;
                double high = excessReturns.Values.DefaultIfEmpty(1.0).Max() + 1.0;
                double target = options.MaxWindowExposureFraction;
                bool solved = false;

                for (int iter = 0; iter < 50; iter++)
                {
                    double mid = (low + high) / 2.0;
                    double sum = 0.0;
                    foreach (var c in activeCandidates)
                    {
                        var raw = (excessReturns[c.CandidateKey] - mid) / variances[c.CandidateKey];
                        var stake = Math.Clamp(raw * options.KellyFraction, 0.0, standaloneStakes[c.CandidateKey]);
                        sum += stake;
                    }

                    if (Math.Abs(sum - target) < 1e-4)
                    {
                        foreach (var c in activeCandidates)
                        {
                            var raw = (excessReturns[c.CandidateKey] - mid) / variances[c.CandidateKey];
                            portfolioStakes[c.CandidateKey] = Math.Clamp(raw * options.KellyFraction, 0.0, standaloneStakes[c.CandidateKey]);
                        }
                        solved = true;
                        break;
                    }

                    if (sum > target)
                    {
                        low = mid;
                    }
                    else
                    {
                        high = mid;
                    }
                }

                if (!solved || portfolioStakes.Values.Sum() <= 1e-6)
                {
                    var scale = target / totalStandalone;
                    foreach (var c in activeCandidates)
                    {
                        portfolioStakes[c.CandidateKey] = Math.Round(standaloneStakes[c.CandidateKey] * scale, 6);
                    }
                }
            }

            var windowExposure = Math.Round(portfolioStakes.Values.Sum(), 4);
            var concurrentCount = activeCandidates.Count;

            foreach (var c in activeCandidates)
            {
                var standalone = standaloneStakes[c.CandidateKey];
                var portfolio = portfolioStakes[c.CandidateKey];
                result[c.CandidateKey] = new SimultaneousKellyAllocation(
                    c.CandidateKey,
                    c.FixtureKey,
                    standalone,
                    portfolio,
                    concurrentCount,
                    windowExposure,
                    wasCapped,
                    ExcludedDueToFixtureExclusivity: false);
            }

            foreach (var c in excludedCandidates)
            {
                var standalone = CalculateFractionalKellyStakeFraction(c.ModelProbability, c.DecimalOdds, options.KellyFraction);
                result[c.CandidateKey] = new SimultaneousKellyAllocation(
                    c.CandidateKey,
                    c.FixtureKey,
                    standalone,
                    PortfolioStakeFraction: 0.0,
                    concurrentCount,
                    windowExposure,
                    WasCapped: true,
                    ExcludedDueToFixtureExclusivity: true);
            }
        }

        return result;
    }
}

public sealed record SimultaneousKellyCandidate(
    string CandidateKey,
    string FixtureKey,
    double ModelProbability,
    double DecimalOdds,
    DateTime KickoffUtc,
    double Edge,
    double ExpectedValuePercent);

public sealed record SimultaneousKellyOptions(
    double MaxWindowExposureFraction = 0.20,
    double WindowToleranceMinutes = 45.0,
    double KellyFraction = BetPricingMath.DefaultKellyFraction,
    bool EnforceFixtureExclusivity = true);

public sealed record SimultaneousKellyAllocation(
    string CandidateKey,
    string FixtureKey,
    double StandaloneStakeFraction,
    double PortfolioStakeFraction,
    int WindowConcurrentBetCount,
    double WindowTotalExposureFraction,
    bool WasCapped,
    bool ExcludedDueToFixtureExclusivity);

