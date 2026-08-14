using System.Text.Json;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Statistics;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.ML;
using Microsoft.ML.Data;

namespace MatchPredictor.Infrastructure.Services;

public sealed class MarketPredictionModelService : IMarketPredictionModelService
{
    private const int MinimumTrainingSamples = 160;
    private const int MinimumHoldoutSamples = 50;
    private const double MinimumBrierImprovement = 0.0015;
    private const double RecencyHalfLifeDays = 30.0;
    private static readonly PredictionMarket[] Markets =
    [
        PredictionMarket.BothTeamsScore,
        PredictionMarket.Over25Goals,
        PredictionMarket.Under25Goals,
        PredictionMarket.HomeWin,
        PredictionMarket.AwayWin,
        PredictionMarket.Draw
    ];
    private static readonly string[] FeatureColumns =
    [
        nameof(MarketModelInput.CalculatorProbability),
        nameof(MarketModelInput.StatisticalProbability),
        nameof(MarketModelInput.BookmakerProbability),
        nameof(MarketModelInput.HomeRestDays),
        nameof(MarketModelInput.AwayRestDays),
        nameof(MarketModelInput.RestDayDifferential),
        nameof(MarketModelInput.HomeFormPoints),
        nameof(MarketModelInput.AwayFormPoints),
        nameof(MarketModelInput.HomeGoalsForPerMatch),
        nameof(MarketModelInput.AwayGoalsForPerMatch),
        nameof(MarketModelInput.HomeGoalsAgainstPerMatch),
        nameof(MarketModelInput.AwayGoalsAgainstPerMatch),
        nameof(MarketModelInput.HeadToHeadHomeWins),
        nameof(MarketModelInput.HeadToHeadDraws),
        nameof(MarketModelInput.HeadToHeadAwayWins),
        nameof(MarketModelInput.HasSnapshot),
        nameof(MarketModelInput.HasStatistical),
        nameof(MarketModelInput.HasBookmaker),
        nameof(MarketModelInput.HasRestDays)
    ];
    private static readonly string ExpectedFeatureSchemaJson = JsonSerializer.Serialize(FeatureColumns);

    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<MarketPredictionModelService> _logger;
    private readonly MLContext _mlContext = new(seed: 42);
    private Dictionary<PredictionMarket, PredictionEngine<MarketModelInput, MarketModelOutput>>? _engines;

    public MarketPredictionModelService(ApplicationDbContext dbContext, ILogger<MarketPredictionModelService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public double? TryPredict(
        MatchData match,
        PredictionMarket market,
        double calculatorProbability,
        double? statisticalProbability,
        double? bookmakerProbability)
    {
        var engines = _engines ??= LoadPromotedEngines();
        if (!engines.TryGetValue(market, out var engine))
        {
            return null;
        }

        var features = LoadLatestFeatureSnapshot(match);
        var output = engine.Predict(MapFeatures(
            calculatorProbability,
            statisticalProbability,
            bookmakerProbability,
            features));

        return Math.Clamp(output.Probability, 0.0f, 1.0f);
    }

    public async Task RebuildProfilesAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-180);
        var forecasts = await _dbContext.ForecastObservations
            .AsNoTracking()
            .Where(forecast =>
                forecast.IsSettled &&
                forecast.OutcomeOccurred != null &&
                (forecast.SettledAt ?? forecast.CreatedAt) >= cutoff)
            .ToListAsync(cancellationToken);
        var featureSnapshots = await _dbContext.FixtureFeatureSnapshots
            .AsNoTracking()
            .Where(snapshot => snapshot.CapturedAtUtc >= cutoff.AddDays(-14))
            .ToListAsync(cancellationToken);
        var pointInTime = PointInTimeBacktestingSelector.SelectForecasts(forecasts);
        var nextProfiles = new List<MarketMlModelProfile>();

        foreach (var market in Markets)
        {
            var rows = pointInTime
                .Where(forecast => forecast.Market == market)
                .OrderBy(forecast => forecast.SettledAt ?? forecast.CreatedAt)
                .Select(forecast => BuildTrainingRow(forecast, featureSnapshots))
                .Where(row => row is not null)
                .Cast<MarketModelInput>()
                .ToList();

            if (rows.Count < MinimumTrainingSamples + MinimumHoldoutSamples)
            {
                continue;
            }

            var splitIndex = Math.Max(MinimumTrainingSamples, (int)Math.Round(rows.Count * 0.7));
            if (splitIndex >= rows.Count - MinimumHoldoutSamples + 1)
            {
                splitIndex = rows.Count - MinimumHoldoutSamples;
            }

            var training = rows.Take(splitIndex).ToList();
            var holdout = rows.Skip(splitIndex).ToList();
            var pipeline = _mlContext.Transforms.Concatenate("Features", FeatureColumns)
                .Append(_mlContext.BinaryClassification.Trainers.LightGbm(
                    labelColumnName: nameof(MarketModelInput.Label),
                    featureColumnName: "Features",
                    exampleWeightColumnName: nameof(MarketModelInput.Weight)));
            var model = pipeline.Fit(_mlContext.Data.LoadFromEnumerable(training));
            var predictions = model.Transform(_mlContext.Data.LoadFromEnumerable(holdout));
            var scored = _mlContext.Data.CreateEnumerable<MarketModelOutput>(predictions, reuseRowObject: false).ToList();

            var baselineBrier = WeightedBrier(holdout, holdout.Select(row => (double)row.CalculatorProbability).ToList());
            var candidateBrier = WeightedBrier(holdout, scored.Select(row => (double)row.Probability).ToList());
            var improvement = baselineBrier - candidateBrier;
            if (improvement < MinimumBrierImprovement)
            {
                _logger.LogInformation(
                    "ML model for {Market} not promoted: candidate Brier {Candidate:F4} vs baseline {Baseline:F4}.",
                    market,
                    candidateBrier,
                    baselineBrier);
                continue;
            }

            await using var stream = new MemoryStream();
            _mlContext.Model.Save(model, _mlContext.Data.LoadFromEnumerable(training).Schema, stream);
            nextProfiles.Add(new MarketMlModelProfile
            {
                Market = market,
                ModelBytes = stream.ToArray(),
                TrainingSampleCount = training.Count,
                HoldoutSampleCount = holdout.Count,
                BaselineBrierScore = baselineBrier,
                CandidateBrierScore = candidateBrier,
                Improvement = improvement,
                IsPromoted = true,
                FeatureSchemaJson = JsonSerializer.Serialize(FeatureColumns),
                UpdatedAtUtc = DateTime.UtcNow
            });
        }

        var promotedMarkets = nextProfiles.Select(profile => profile.Market).ToHashSet();
        var retainedProfiles = await _dbContext.MarketMlModelProfiles
            .AsNoTracking()
            .Where(profile => profile.IsPromoted && !promotedMarkets.Contains(profile.Market))
            .ToListAsync(cancellationToken);
        nextProfiles.AddRange(retainedProfiles);

        try
        {
            await _dbContext.MarketMlModelProfiles.ExecuteDeleteAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            var existing = await _dbContext.MarketMlModelProfiles.ToListAsync(cancellationToken);
            _dbContext.MarketMlModelProfiles.RemoveRange(existing);
        }

        await _dbContext.MarketMlModelProfiles.AddRangeAsync(nextProfiles, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _engines = null;
    }

    private Dictionary<PredictionMarket, PredictionEngine<MarketModelInput, MarketModelOutput>> LoadPromotedEngines()
    {
        var engines = new Dictionary<PredictionMarket, PredictionEngine<MarketModelInput, MarketModelOutput>>();
        foreach (var profile in _dbContext.MarketMlModelProfiles.AsNoTracking().Where(profile => profile.IsPromoted))
        {
            if (!string.Equals(profile.FeatureSchemaJson, ExpectedFeatureSchemaJson, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Skipping promoted ML model for {Market} because feature schema changed.",
                    profile.Market);
                continue;
            }

            using var stream = new MemoryStream(profile.ModelBytes);
            var model = _mlContext.Model.Load(stream, out _);
            engines[profile.Market] = _mlContext.Model.CreatePredictionEngine<MarketModelInput, MarketModelOutput>(model);
        }

        return engines;
    }

    private FixtureFeatureSnapshot? LoadLatestFeatureSnapshot(MatchData match)
    {
        if (string.IsNullOrWhiteSpace(match.FixtureKey))
        {
            return null;
        }

        return _dbContext.FixtureFeatureSnapshots
            .AsNoTracking()
            .Where(snapshot => snapshot.FixtureKey == match.FixtureKey)
            .OrderByDescending(snapshot => snapshot.CapturedAtUtc)
            .FirstOrDefault();
    }

    internal static MarketModelInput? BuildTrainingRow(
        ForecastObservation forecast,
        IReadOnlyCollection<FixtureFeatureSnapshot> featureSnapshots)
    {
        var signals = ParseSignals(forecast.FeatureContributionsJson, forecast.RawProbability);
        var featureSnapshot = featureSnapshots
            .Where(snapshot => string.Equals(snapshot.FixtureKey, forecast.FixtureKey, StringComparison.OrdinalIgnoreCase))
            .Where(snapshot => snapshot.CapturedAtUtc <= (forecast.MatchDateTime ?? forecast.CreatedAt))
            .OrderByDescending(snapshot => snapshot.CapturedAtUtc)
            .FirstOrDefault();

        return MapFeatures(
            signals.Calculator,
            signals.Statistical,
            signals.Bookmaker,
            featureSnapshot,
            forecast.OutcomeOccurred == true,
            (float)RecencyWeighting.CalculateWeight(forecast.SettledAt ?? forecast.CreatedAt, RecencyHalfLifeDays));
    }

    internal static MarketModelInput MapFeatures(
        double calculatorProbability,
        double? statisticalProbability,
        double? bookmakerProbability,
        FixtureFeatureSnapshot? featureSnapshot,
        bool label = false,
        float weight = 1f)
    {
        var hasSnapshot = featureSnapshot is not null;
        var hasRestDays = featureSnapshot?.HomeRestDays is not null && featureSnapshot?.AwayRestDays is not null;
        var homeRestDays = ToFeature(featureSnapshot?.HomeRestDays);
        var awayRestDays = ToFeature(featureSnapshot?.AwayRestDays);

        return new MarketModelInput
        {
            Label = label,
            Weight = weight,
            CalculatorProbability = (float)calculatorProbability,
            StatisticalProbability = ToFeature(statisticalProbability),
            BookmakerProbability = ToFeature(bookmakerProbability),
            HomeRestDays = homeRestDays,
            AwayRestDays = awayRestDays,
            RestDayDifferential = hasRestDays ? homeRestDays - awayRestDays : float.NaN,
            HomeFormPoints = ToFeature(featureSnapshot?.HomeFormPointsPerMatch),
            AwayFormPoints = ToFeature(featureSnapshot?.AwayFormPointsPerMatch),
            HomeGoalsForPerMatch = ToFeature(featureSnapshot?.HomeFormGoalsForPerMatch),
            AwayGoalsForPerMatch = ToFeature(featureSnapshot?.AwayFormGoalsForPerMatch),
            HomeGoalsAgainstPerMatch = ToFeature(featureSnapshot?.HomeFormGoalsAgainstPerMatch),
            AwayGoalsAgainstPerMatch = ToFeature(featureSnapshot?.AwayFormGoalsAgainstPerMatch),
            HeadToHeadHomeWins = hasSnapshot ? featureSnapshot!.HeadToHeadHomeWins : float.NaN,
            HeadToHeadDraws = hasSnapshot ? featureSnapshot!.HeadToHeadDraws : float.NaN,
            HeadToHeadAwayWins = hasSnapshot ? featureSnapshot!.HeadToHeadAwayWins : float.NaN,
            HasSnapshot = hasSnapshot ? 1f : 0f,
            HasStatistical = statisticalProbability is not null ? 1f : 0f,
            HasBookmaker = bookmakerProbability is not null ? 1f : 0f,
            HasRestDays = hasRestDays ? 1f : 0f
        };
    }

    private static float ToFeature(double? value) => value is double number ? (float)number : float.NaN;

    internal static (double Calculator, double? Statistical, double? Bookmaker) ParseSignals(string? json, double fallback)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
        {
            return (fallback, null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var market = ReadString(document.RootElement, "market");
            var calculator = ReadSignal(document.RootElement, "calculatorSignal", market) ?? fallback;
            var statistical = ReadSignal(document.RootElement, "statisticalSignal", market);
            var bookmaker = ReadSignal(document.RootElement, "bookmakerSignal", market);
            return (calculator, statistical, bookmaker);
        }
        catch
        {
            return (fallback, null, null);
        }
    }

    internal static double? ReadSignal(JsonElement root, string sectionName, string? market)
    {
        if (!root.TryGetProperty(sectionName, out var section) || section.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var (propertyName, complement) = market switch
        {
            nameof(PredictionMarket.BothTeamsScore) => ("btts", false),
            nameof(PredictionMarket.Over25Goals) => ("over25", false),
            nameof(PredictionMarket.Under25Goals) => ("under25", false),
            nameof(PredictionMarket.HomeWin) => ("homeWin", false),
            nameof(PredictionMarket.AwayWin) => ("awayWin", false),
            nameof(PredictionMarket.Draw) => ("draw", false),
            _ => (string.Empty, false)
        };

        if (TryReadProbability(section, propertyName, out var probability))
        {
            return complement ? 1.0 - probability : probability;
        }

        if (market == nameof(PredictionMarket.Under25Goals) &&
            TryReadProbability(section, "over25", out var over25))
        {
            return 1.0 - over25;
        }

        return null;
    }

    private static bool TryReadProbability(JsonElement section, string propertyName, out double probability)
    {
        probability = 0;
        return propertyName.Length > 0 &&
               section.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetDouble(out probability);
    }

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static double WeightedBrier(IReadOnlyList<MarketModelInput> rows, IReadOnlyList<double> probabilities)
    {
        var weightedError = rows.Select((row, index) =>
        {
            var outcome = row.Label ? 1.0 : 0.0;
            return Math.Pow(Math.Clamp(probabilities[index], 0.0, 1.0) - outcome, 2) * Math.Max(row.Weight, 1e-6f);
        }).Sum();
        var totalWeight = rows.Sum(row => Math.Max(row.Weight, 1e-6f));
        return weightedError / Math.Max(totalWeight, 1e-9);
    }

    public sealed class MarketModelInput
    {
        public bool Label { get; set; }
        public float Weight { get; set; }
        public float CalculatorProbability { get; set; }
        public float StatisticalProbability { get; set; }
        public float BookmakerProbability { get; set; }
        public float HomeRestDays { get; set; }
        public float AwayRestDays { get; set; }
        public float RestDayDifferential { get; set; }
        public float HomeFormPoints { get; set; }
        public float AwayFormPoints { get; set; }
        public float HomeGoalsForPerMatch { get; set; }
        public float AwayGoalsForPerMatch { get; set; }
        public float HomeGoalsAgainstPerMatch { get; set; }
        public float AwayGoalsAgainstPerMatch { get; set; }
        public float HeadToHeadHomeWins { get; set; }
        public float HeadToHeadDraws { get; set; }
        public float HeadToHeadAwayWins { get; set; }
        public float HasSnapshot { get; set; }
        public float HasStatistical { get; set; }
        public float HasBookmaker { get; set; }
        public float HasRestDays { get; set; }
    }

    private sealed class MarketModelOutput
    {
        [ColumnName("Probability")]
        public float Probability { get; set; }
    }
}
