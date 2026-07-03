using System.Text.Json;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Statistics;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatchPredictor.Infrastructure.Services;

/// <summary>
/// Learns per-market ensemble stacking weights from settled forecast observations.
///
/// Each settled observation's FeatureContributionsJson records the per-signal probabilities
/// (bookmaker, calculator/feed, Dixon-Coles statistical core) that fed its blend. This service
/// replays those signals through the same logit blender over a deterministic weight grid,
/// selects the training-window winner by recency-weighted log-loss, and promotes it only when
/// it beats the incumbent weights' Brier score on a chronological holdout split. Unpromoted
/// markets keep the configured defaults, so sparse data degrades gracefully.
/// </summary>
public class EnsembleWeightTuningService : IEnsembleWeightTuningService
{
    private const int EvaluationWindowDays = 90;
    private const double RecencyHalfLifeDays = 30.0;
    private const int MinimumTrainingSampleCount = 120;
    private const int MinimumHoldoutSampleCount = 40;
    private const double MinimumBrierImprovement = 0.0015;

    private static readonly double[] WeightGrid = [0.0, 0.3, 0.6, 0.9, 1.2, 1.5, 1.8];

    private static readonly PredictionMarket[] TunedMarkets =
    [
        PredictionMarket.BothTeamsScore,
        PredictionMarket.Over25Goals,
        PredictionMarket.Under25Goals,
        PredictionMarket.HomeWin,
        PredictionMarket.AwayWin,
        PredictionMarket.Draw
    ];

    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<EnsembleWeightTuningService> _logger;
    private Dictionary<PredictionMarket, EnsembleWeightProfile>? _profiles;

    public EnsembleWeightTuningService(ApplicationDbContext dbContext, ILogger<EnsembleWeightTuningService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    private Dictionary<PredictionMarket, EnsembleWeightProfile> Profiles =>
        _profiles ??= _dbContext.EnsembleWeightProfiles
            .AsNoTracking()
            .ToList()
            .GroupBy(profile => profile.Market)
            .ToDictionary(group => group.Key, group => group.First());

    public EnsembleWeights GetWeights(PredictionMarket market, EnsembleWeights fallback)
    {
        if (!Profiles.TryGetValue(market, out var profile))
        {
            return fallback;
        }

        return fallback with
        {
            Bookmaker = profile.BookmakerWeight,
            Market = profile.CalculatorWeight,
            DixonColes = profile.DixonColesWeight
        };
    }

    public async Task RebuildProfilesAsync()
    {
        var cutoff = DateTime.UtcNow.AddDays(-EvaluationWindowDays);
        var forecasts = await _dbContext.ForecastObservations
            .AsNoTracking()
            .Where(forecast =>
                forecast.IsSettled &&
                forecast.OutcomeOccurred != null &&
                (forecast.SettledAt ?? forecast.CreatedAt) >= cutoff)
            .ToListAsync();

        var pointInTimeForecasts = PointInTimeBacktestingSelector.SelectForecasts(forecasts);
        var rebuiltProfiles = new List<EnsembleWeightProfile>();

        foreach (var market in TunedMarkets)
        {
            var samples = pointInTimeForecasts
                .Where(forecast => forecast.Market == market)
                .OrderBy(forecast => forecast.SettledAt ?? forecast.CreatedAt)
                .Select(forecast => BuildSample(forecast, market))
                .Where(sample => sample is not null)
                .Cast<SignalSample>()
                .ToList();

            if (samples.Count < MinimumTrainingSampleCount + MinimumHoldoutSampleCount)
            {
                continue;
            }

            var splitIndex = Math.Max(
                MinimumTrainingSampleCount,
                (int)Math.Round(samples.Count * 0.7));
            if (splitIndex >= samples.Count - MinimumHoldoutSampleCount + 1)
            {
                splitIndex = samples.Count - MinimumHoldoutSampleCount;
            }

            var training = samples.Take(splitIndex).ToList();
            var holdout = samples.Skip(splitIndex).ToList();

            // Compare against what production is actually running today: a promoted profile
            // when one exists, otherwise the configured ProductionDefault stacking.
            var incumbent = ResolvePromotionIncumbent(market);
            var candidate = SelectBestWeights(training);
            if (candidate is null)
            {
                continue;
            }

            var incumbentHoldoutBrier = EvaluateBrier(holdout, incumbent);
            var candidateHoldoutBrier = EvaluateBrier(holdout, candidate);

            if (candidateHoldoutBrier >= incumbentHoldoutBrier - MinimumBrierImprovement)
            {
                _logger.LogInformation(
                    "Ensemble weights for {Market} not promoted: candidate holdout Brier {Candidate:F4} vs incumbent {Incumbent:F4}.",
                    market,
                    candidateHoldoutBrier,
                    incumbentHoldoutBrier);
                continue;
            }

            rebuiltProfiles.Add(new EnsembleWeightProfile
            {
                Market = market,
                BookmakerWeight = candidate.Bookmaker,
                CalculatorWeight = candidate.Market,
                DixonColesWeight = candidate.DixonColes,
                SampleCount = training.Count,
                HoldoutCount = holdout.Count,
                BaselineHoldoutBrier = incumbentHoldoutBrier,
                CandidateHoldoutBrier = candidateHoldoutBrier,
                UpdatedAt = DateTime.UtcNow
            });

            _logger.LogInformation(
                "Promoted ensemble weights for {Market}: bookmaker={Bookmaker:F2} calculator={Calculator:F2} dixonColes={DixonColes:F2} (holdout Brier {Candidate:F4} vs {Incumbent:F4}).",
                market,
                candidate.Bookmaker,
                candidate.Market,
                candidate.DixonColes,
                candidateHoldoutBrier,
                incumbentHoldoutBrier);
        }

        // Keep incumbent profiles for markets that did not earn a new promotion this run
        // but previously had one — deleting them would silently revert to defaults.
        var promotedMarkets = rebuiltProfiles.Select(profile => profile.Market).ToHashSet();
        var retainedProfiles = Profiles.Values
            .Where(profile => !promotedMarkets.Contains(profile.Market))
            .Select(profile => new EnsembleWeightProfile
            {
                Market = profile.Market,
                BookmakerWeight = profile.BookmakerWeight,
                CalculatorWeight = profile.CalculatorWeight,
                DixonColesWeight = profile.DixonColesWeight,
                SampleCount = profile.SampleCount,
                HoldoutCount = profile.HoldoutCount,
                BaselineHoldoutBrier = profile.BaselineHoldoutBrier,
                CandidateHoldoutBrier = profile.CandidateHoldoutBrier,
                UpdatedAt = profile.UpdatedAt
            })
            .ToList();

        var nextProfiles = rebuiltProfiles.Concat(retainedProfiles).ToList();

        try
        {
            await _dbContext.EnsembleWeightProfiles.ExecuteDeleteAsync();
        }
        catch (InvalidOperationException)
        {
            var existing = await _dbContext.EnsembleWeightProfiles.ToListAsync();
            _dbContext.EnsembleWeightProfiles.RemoveRange(existing);
        }

        await _dbContext.EnsembleWeightProfiles.AddRangeAsync(nextProfiles);
        await _dbContext.SaveChangesAsync();

        _profiles = nextProfiles
            .GroupBy(profile => profile.Market)
            .ToDictionary(group => group.Key, group => group.First());
    }

    private static EnsembleWeights? SelectBestWeights(IReadOnlyList<SignalSample> training)
    {
        EnsembleWeights? best = null;
        var bestLogLoss = double.MaxValue;

        foreach (var bookmakerWeight in WeightGrid)
        {
            foreach (var calculatorWeight in WeightGrid)
            {
                foreach (var dixonColesWeight in WeightGrid)
                {
                    if (bookmakerWeight + calculatorWeight + dixonColesWeight <= 0)
                    {
                        continue;
                    }

                    var weights = new EnsembleWeights
                    {
                        Bookmaker = bookmakerWeight,
                        Market = calculatorWeight,
                        Base = 0.0,
                        DixonColes = dixonColesWeight,
                        Elo = 0.0
                    };

                    var logLoss = EvaluateLogLoss(training, weights);
                    if (logLoss < bestLogLoss)
                    {
                        bestLogLoss = logLoss;
                        best = weights;
                    }
                }
            }
        }

        return best;
    }

    private static double EvaluateLogLoss(IReadOnlyList<SignalSample> samples, EnsembleWeights weights)
    {
        var weightedLoss = 0.0;
        var totalWeight = 0.0;

        foreach (var sample in samples)
        {
            var probability = BlendSample(sample, weights);
            var clamped = Math.Clamp(probability, 1e-6, 1.0 - 1e-6);
            var loss = sample.Outcome ? -Math.Log(clamped) : -Math.Log(1.0 - clamped);
            weightedLoss += loss * sample.RecencyWeight;
            totalWeight += sample.RecencyWeight;
        }

        return totalWeight > 0 ? weightedLoss / totalWeight : double.MaxValue;
    }

    private static double EvaluateBrier(IReadOnlyList<SignalSample> samples, EnsembleWeights weights)
    {
        var weightedError = 0.0;
        var totalWeight = 0.0;

        foreach (var sample in samples)
        {
            var probability = BlendSample(sample, weights);
            var error = Math.Pow(probability - (sample.Outcome ? 1.0 : 0.0), 2);
            weightedError += error * sample.RecencyWeight;
            totalWeight += sample.RecencyWeight;
        }

        return totalWeight > 0 ? weightedError / totalWeight : double.MaxValue;
    }

    private static double BlendSample(SignalSample sample, EnsembleWeights weights)
    {
        return EnsembleProbabilityBlender.BlendLogit(
            (sample.Bookmaker, weights.Bookmaker),
            (sample.Calculator, weights.Market),
            (sample.DixonColes, weights.DixonColes));
    }

    /// <summary>
    /// Incumbent weights for the promotion gate — matches live inference defaults until
    /// a market-specific profile has already been promoted.
    /// </summary>
    private EnsembleWeights ResolvePromotionIncumbent(PredictionMarket market)
    {
        if (Profiles.TryGetValue(market, out var profile))
        {
            return new EnsembleWeights
            {
                Bookmaker = profile.BookmakerWeight,
                Market = profile.CalculatorWeight,
                Base = 0.0,
                DixonColes = profile.DixonColesWeight,
                Elo = 0.0
            };
        }

        return EnsembleWeights.ProductionDefault;
    }

    private static SignalSample? BuildSample(ForecastObservation forecast, PredictionMarket market)
    {
        if (string.IsNullOrWhiteSpace(forecast.FeatureContributionsJson) ||
            forecast.FeatureContributionsJson == "{}")
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(forecast.FeatureContributionsJson);
            var root = document.RootElement;

            var calculator = ReadMarketProbability(root, "calculatorSignal", market);
            if (calculator is null)
            {
                // Older observations predate per-signal logging; they cannot be replayed.
                return null;
            }

            return new SignalSample(
                Bookmaker: ReadMarketProbability(root, "bookmakerSignal", market),
                Calculator: calculator,
                DixonColes: ReadMarketProbability(root, "statisticalSignal", market),
                Outcome: forecast.OutcomeOccurred == true,
                RecencyWeight: RecencyWeighting.CalculateWeight(
                    forecast.SettledAt ?? forecast.CreatedAt,
                    RecencyHalfLifeDays));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double? ReadMarketProbability(JsonElement root, string signalKey, PredictionMarket market)
    {
        if (!root.TryGetProperty(signalKey, out var signal) || signal.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var (propertyName, complement) = market switch
        {
            PredictionMarket.BothTeamsScore => ("btts", false),
            PredictionMarket.Over25Goals => ("over25", false),
            PredictionMarket.Under25Goals => ("over25", true),
            PredictionMarket.HomeWin => ("homeWin", false),
            PredictionMarket.AwayWin => ("awayWin", false),
            PredictionMarket.Draw => ("draw", false),
            _ => (string.Empty, false)
        };

        if (propertyName.Length == 0 ||
            !signal.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        var probability = value.GetDouble();
        return complement ? 1.0 - probability : probability;
    }

    private sealed record SignalSample(
        double? Bookmaker,
        double? Calculator,
        double? DixonColes,
        bool Outcome,
        double RecencyWeight);
}
