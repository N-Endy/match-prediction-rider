using Hangfire;
using Hangfire.PostgreSql;
using MatchPredictor.Application.Services;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Services;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

var mode = args.FirstOrDefault()?.Trim().ToLowerInvariant() switch
{
    "--benchmark" or "benchmark" => RunnerMode.Benchmark,
    "--score-backfill" or "score-backfill" => RunnerMode.ScoreBackfill,
    "--score" or "score" => RunnerMode.Score,
    "--export-history" or "export-history" => RunnerMode.ExportHistory,
    "--import-history" or "import-history" => RunnerMode.ImportHistory,
    _ => RunnerMode.Sync
};

var rawConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
    ?? Environment.GetEnvironmentVariable("MATCHPREDICTOR_DEV_DB")
    ?? throw new InvalidOperationException(
        "Set ConnectionStrings__DefaultConnection or MATCHPREDICTOR_DEV_DB before running this tool.");

var connectionString = NormalizeConnectionString(rawConnectionString);

var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("MatchPredictor.Web/appsettings.json", optional: false)
    .AddJsonFile("MatchPredictor.Web/appsettings.Production.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var services = new ServiceCollection();
services.AddSingleton<IConfiguration>(configuration);
services.AddLogging(builder =>
{
    builder
        .AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        })
        .SetMinimumLevel(LogLevel.Information);
});
services.Configure<PredictionSettings>(configuration.GetSection("PredictionSettings"));
services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(connectionString));
services.AddHttpClient("SportyBet", client => client.Timeout = TimeSpan.FromSeconds(30));
services.AddDistributedMemoryCache();
services.AddSingleton<AiScoreSourceHealthTracker>();
services.AddSingleton<SofaScoreSourceHealthTracker>();
services.AddSingleton<FlashScoreSourceHealthTracker>();
services.AddScoped<IHistoricalDatasetService, HistoricalDatasetService>();
services.AddScoped<IDataAnalyzerService, DataAnalyzerService>();
services.AddScoped<ISportsAiExcelScraper, SportsAiExcelScraper>();
services.AddScoped<IWebScraperService, WebScraperService>();
if (mode is RunnerMode.Score or RunnerMode.ScoreBackfill)
{
    // Score-only modes never read the workbook, so avoid requiring an EPPlus license.
    services.AddScoped<IExtractFromExcel, NoOpExtractFromExcel>();
}
else
{
    services.AddScoped<IExtractFromExcel, ExtractFromExcel>();
}
services.AddScoped<IProbabilityCalculator, ProbabilityCalculator>();
services.AddScoped<ICalibrationService, CalibrationService>();
services.AddScoped<IThresholdTuningService, ThresholdTuningService>();
services.AddScoped<IRegressionPredictorService, RegressionPredictorService>();
services.AddScoped<SportyBetBookingService>();
services.AddScoped<ISourceMarketPricingService>(provider => provider.GetRequiredService<SportyBetBookingService>());
services.AddScoped<IAnalyzerService, AnalyzerService>();

GlobalConfiguration.Configuration.UsePostgreSqlStorage(
    connectionString,
    new PostgreSqlStorageOptions
    {
        SchemaName = "hangfire",
        QueuePollInterval = TimeSpan.FromSeconds(15),
        PrepareSchemaIfNecessary = true,
        DistributedLockTimeout = TimeSpan.FromMinutes(1),
        TransactionSynchronisationTimeout = TimeSpan.FromMinutes(1)
    });

await using var serviceProvider = services.BuildServiceProvider();
await using var scope = serviceProvider.CreateAsyncScope();

var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

Console.WriteLine($"Mode: {mode}");

if (mode is RunnerMode.ExportHistory)
{
    var datasetService = scope.ServiceProvider.GetRequiredService<IHistoricalDatasetService>();
    var lookbackDays = args.Length > 1 && int.TryParse(args[1], out var parsedLookback) ? parsedLookback : 540;
    var outputPath = args.Length > 2 ? args[2] : "historical-dataset.json";
    var json = await datasetService.ExportAsync(lookbackDays);
    await File.WriteAllTextAsync(outputPath, json);
    Console.WriteLine($"Exported historical dataset ({lookbackDays}d) to {Path.GetFullPath(outputPath)}");
    return;
}

if (mode is RunnerMode.ImportHistory)
{
    if (args.Length < 2)
    {
        Console.WriteLine("Usage: --import-history <path-to-dataset.json> [--replace]");
        return;
    }

    var datasetService = scope.ServiceProvider.GetRequiredService<IHistoricalDatasetService>();
    var inputPath = args[1];
    var replaceExisting = args.Any(arg => string.Equals(arg, "--replace", StringComparison.OrdinalIgnoreCase));
    var json = await File.ReadAllTextAsync(inputPath);
    var imported = await datasetService.ImportAsync(json, replaceExisting);
    Console.WriteLine($"Imported {imported} historical match row(s) from {Path.GetFullPath(inputPath)} (replace={replaceExisting}).");
    return;
}

switch (mode)
{
    case RunnerMode.Benchmark:
        await RunBenchmarkAsync(context, scope.ServiceProvider.GetRequiredService<IProbabilityCalculator>());
        return;
    case RunnerMode.Score:
        var analyzer = scope.ServiceProvider.GetRequiredService<IAnalyzerService>();
        Console.WriteLine("Before:");
        await PrintMissingSummaryAsync(context);
        await PrintBttsSummaryAsync(context);
        await analyzer.RunScoreUpdaterAsync();
        break;
    case RunnerMode.ScoreBackfill:
        analyzer = scope.ServiceProvider.GetRequiredService<IAnalyzerService>();
        Console.WriteLine("Before:");
        await PrintMissingSummaryAsync(context);
        await PrintBttsSummaryAsync(context);
        await analyzer.RunScoreUpdaterAsync(14, "backfill");
        break;
    case RunnerMode.Sync:
        analyzer = scope.ServiceProvider.GetRequiredService<IAnalyzerService>();
        Console.WriteLine("Before:");
        await PrintMissingSummaryAsync(context);
        await PrintBttsSummaryAsync(context);
        await analyzer.ExtractDataAndSyncDatabaseAsync();
        break;
}

context.ChangeTracker.Clear();

Console.WriteLine();
Console.WriteLine("After:");
await PrintMissingSummaryAsync(context);
await PrintBttsSummaryAsync(context);

static async Task PrintMissingSummaryAsync(ApplicationDbContext context)
{
    var recent = await context.Predictions
        .AsNoTracking()
        .Select(prediction => new
        {
            prediction.Date,
            prediction.ActualScore,
            prediction.MatchDateTime
        })
        .ToListAsync();

    var now = DateTime.UtcNow;

    var summary = recent
        .GroupBy(prediction => prediction.Date)
        .Select(group => new
        {
            Date = group.Key,
            Total = group.Count(),
            Missing = group.Count(prediction => string.IsNullOrWhiteSpace(prediction.ActualScore)),
            OverdueMissing = group.Count(prediction =>
                string.IsNullOrWhiteSpace(prediction.ActualScore) &&
                prediction.MatchDateTime.HasValue &&
                prediction.MatchDateTime.Value < now.AddHours(-4))
        })
        .OrderByDescending(group => DateTime.TryParseExact(
            group.Date,
            "dd-MM-yyyy",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out var parsedDate)
            ? parsedDate
            : DateTime.MinValue)
        .Take(10);

    foreach (var row in summary)
    {
        Console.WriteLine($"{row.Date}: total={row.Total}, missing={row.Missing}, overdueMissing={row.OverdueMissing}");
    }
}

static async Task PrintBttsSummaryAsync(ApplicationDbContext context)
{
    var today = DateTimeProvider.GetLocalTime().ToString("dd-MM-yyyy");

    var todayMatches = await context.MatchDatas
        .AsNoTracking()
        .Where(match => match.Date == today)
        .ToListAsync();

    var populated = todayMatches.Count(match => match.BttsYes > 0 && match.BttsNo > 0);
    Console.WriteLine($"BTTS source pricing for {today}: totalMatches={todayMatches.Count}, populated={populated}");

    foreach (var sample in todayMatches
                 .Where(match => match.BttsYes > 0 && match.BttsNo > 0)
                 .OrderByDescending(match => match.BttsYes)
                 .Take(5))
    {
        Console.WriteLine(
            $"  {sample.HomeTeam} vs {sample.AwayTeam} [{sample.League}] -> yes={sample.BttsYes:F3}, no={sample.BttsNo:F3}");
    }
}

static string NormalizeConnectionString(string rawConnectionString)
{
    if (!rawConnectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
        !rawConnectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
    {
        return rawConnectionString;
    }

    var uri = new Uri(rawConnectionString);
    var userInfo = uri.UserInfo.Split(':', 2);

    var builder = new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.Port > 0 ? uri.Port : 5432,
        Username = userInfo.Length > 0 ? Uri.UnescapeDataString(userInfo[0]) : string.Empty,
        Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty,
        Database = uri.AbsolutePath.Trim('/'),
    };

    foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
    {
        var pieces = pair.Split('=', 2);
        if (pieces.Length != 2) continue;

        var key = Uri.UnescapeDataString(pieces[0]);
        var value = Uri.UnescapeDataString(pieces[1]);

        switch (key.ToLowerInvariant())
        {
            case "sslmode" when Enum.TryParse<SslMode>(value, true, out var sslMode):
                builder.SslMode = sslMode;
                break;
            case "channel_binding" when Enum.TryParse<ChannelBinding>(value, true, out var channelBinding):
                builder.ChannelBinding = channelBinding;
                break;
        }
    }

    return builder.ConnectionString;
}

static async Task RunBenchmarkAsync(ApplicationDbContext context, IProbabilityCalculator probabilityCalculator)
{
    const int lookbackDays = 60;
    var cutoffLocalDate = DateOnly.FromDateTime(DateTimeProvider.GetLocalTime().Date.AddDays(-lookbackDays));

    var settledForecasts = await context.ForecastObservations
        .AsNoTracking()
        .Where(forecast =>
            forecast.IsSettled &&
            forecast.OutcomeOccurred != null &&
            forecast.MatchLocalDate >= cutoffLocalDate)
        .ToListAsync();

    var selectedForecasts = SelectPointInTimeForecasts(settledForecasts);
    var matchDataRows = await context.MatchDatas
        .AsNoTracking()
        .Where(match => match.MatchLocalDate != null && match.MatchLocalDate >= cutoffLocalDate)
        .ToListAsync();

    var matchLookup = matchDataRows
        .GroupBy(match => BuildMatchLookupKey(
            match.FixtureKey,
            match.MatchLocalDate,
            match.League,
            match.HomeTeam,
            match.AwayTeam))
        .ToDictionary(group => group.Key, group => group
            .OrderByDescending(match => match.MatchDateTime ?? DateTime.MinValue)
            .First());

    var legacyCalculator = new LegacyProbabilityCalculator();
    var marketStats = new Dictionary<PredictionMarket, BenchmarkAccumulator>();
    var leagueStatsByMarket = new Dictionary<PredictionMarket, Dictionary<string, BenchmarkAccumulator>>();
    var bandStatsByMarket = new Dictionary<PredictionMarket, Dictionary<string, BenchmarkAccumulator>>();
    var unmatched = 0;

    foreach (var forecast in selectedForecasts)
    {
        var lookupKey = BuildMatchLookupKey(
            forecast.FixtureKey,
            forecast.MatchLocalDate,
            forecast.League,
            forecast.HomeTeam,
            forecast.AwayTeam);

        if (!matchLookup.TryGetValue(lookupKey, out var match))
        {
            unmatched++;
            continue;
        }

        var actual = forecast.OutcomeOccurred == true ? 1.0 : 0.0;
        var storedRaw = Math.Clamp(forecast.RawProbability, 0.0, 1.0);
        var legacyRaw = CalculateProbabilityByMarket(legacyCalculator, match, forecast.Market);
        var modelV2Raw = CalculateProbabilityByMarket(probabilityCalculator, match, forecast.Market);

        if (!marketStats.TryGetValue(forecast.Market, out var accumulator))
        {
            accumulator = new BenchmarkAccumulator();
            marketStats[forecast.Market] = accumulator;
        }

        accumulator.Add(actual, storedRaw, legacyRaw, modelV2Raw);

        if (!leagueStatsByMarket.TryGetValue(forecast.Market, out var leagueStats))
        {
            leagueStats = new Dictionary<string, BenchmarkAccumulator>(StringComparer.OrdinalIgnoreCase);
            leagueStatsByMarket[forecast.Market] = leagueStats;
        }

        var leagueKey = NormalizeLeagueLabel(forecast.League);
        if (!leagueStats.TryGetValue(leagueKey, out var leagueAccumulator))
        {
            leagueAccumulator = new BenchmarkAccumulator();
            leagueStats[leagueKey] = leagueAccumulator;
        }

        leagueAccumulator.Add(actual, storedRaw, legacyRaw, modelV2Raw);

        if (!bandStatsByMarket.TryGetValue(forecast.Market, out var bandStats))
        {
            bandStats = new Dictionary<string, BenchmarkAccumulator>(StringComparer.OrdinalIgnoreCase);
            bandStatsByMarket[forecast.Market] = bandStats;
        }

        var bandKey = GetConfidenceBandLabel(forecast.CalibratedProbability);
        if (!bandStats.TryGetValue(bandKey, out var bandAccumulator))
        {
            bandAccumulator = new BenchmarkAccumulator();
            bandStats[bandKey] = bandAccumulator;
        }

        bandAccumulator.Add(actual, storedRaw, legacyRaw, modelV2Raw);
    }

    Console.WriteLine($"Benchmark window: last {lookbackDays} days");
    Console.WriteLine($"Point-in-time settled forecasts: {selectedForecasts.Count}");
    Console.WriteLine($"Matched to MatchData rows: {selectedForecasts.Count - unmatched}");
    Console.WriteLine($"Unmatched forecasts skipped: {unmatched}");
    Console.WriteLine();
    Console.WriteLine("Market                         Samples  StoredRaw  LegacyRaw  ModelV2   DeltaVsStored  DeltaVsLegacy");

    foreach (var market in marketStats.Keys.OrderBy(m => m))
    {
        var stats = marketStats[market];
        var storedBrier = stats.StoredRawBrier;
        var legacyBrier = stats.LegacyRawBrier;
        var v2Brier = stats.ModelV2RawBrier;
        Console.WriteLine(
            $"{market.ToDisplayName(),-30} {stats.Count,7}  {storedBrier,8:F4}  {legacyBrier,8:F4}  {v2Brier,8:F4}  {(storedBrier - v2Brier),13:+0.0000;-0.0000;0.0000}  {(legacyBrier - v2Brier),13:+0.0000;-0.0000;0.0000}");
    }

    Console.WriteLine();
    Console.WriteLine("Note: `StoredRaw` is the historical point-in-time raw probability already saved on settled forecasts.");
    Console.WriteLine("`LegacyRaw` and `ModelV2` are recomputed today from currently stored MatchData for the same fixtures.");

    Console.WriteLine();
    Console.WriteLine("Detailed league/confidence breakdowns for markets where ModelV2 trails LegacyRaw:");

    foreach (var market in marketStats.Keys.OrderBy(m => m))
    {
        var overall = marketStats[market];
        var deltaVsLegacy = overall.LegacyRawBrier - overall.ModelV2RawBrier;
        if (deltaVsLegacy >= 0)
        {
            continue;
        }

        Console.WriteLine();
        Console.WriteLine($"{market.ToDisplayName()}");
        Console.WriteLine($"Overall delta vs legacy: {deltaVsLegacy:+0.0000;-0.0000;0.0000}");

        if (leagueStatsByMarket.TryGetValue(market, out var leagueStats))
        {
            var worstLeagueRows = leagueStats
                .Where(row => row.Value.Count >= 8)
                .Select(row => new
                {
                    League = row.Key,
                    Stats = row.Value,
                    Delta = row.Value.LegacyRawBrier - row.Value.ModelV2RawBrier
                })
                .Where(row => row.Delta < 0)
                .OrderBy(row => row.Delta)
                .ThenByDescending(row => row.Stats.Count)
                .Take(5)
                .ToList();

            if (worstLeagueRows.Count > 0)
            {
                Console.WriteLine("  Worst leagues vs legacy (min 8 samples):");
                foreach (var row in worstLeagueRows)
                {
                    Console.WriteLine(
                        $"    {row.League,-35} n={row.Stats.Count,3} legacy={row.Stats.LegacyRawBrier:F4} v2={row.Stats.ModelV2RawBrier:F4} delta={row.Delta:+0.0000;-0.0000;0.0000}");
                }
            }
        }

        if (bandStatsByMarket.TryGetValue(market, out var bandStats))
        {
            var bandRows = bandStats
                .Select(row => new
                {
                    Band = row.Key,
                    Stats = row.Value,
                    Delta = row.Value.LegacyRawBrier - row.Value.ModelV2RawBrier,
                    SortKey = GetConfidenceBandSortKey(row.Key)
                })
                .Where(row => row.Stats.Count >= 10)
                .OrderBy(row => row.SortKey)
                .ToList();

            if (bandRows.Count > 0)
            {
                Console.WriteLine("  Confidence bands vs legacy:");
                foreach (var row in bandRows)
                {
                    Console.WriteLine(
                        $"    {row.Band,-12} n={row.Stats.Count,3} legacy={row.Stats.LegacyRawBrier:F4} v2={row.Stats.ModelV2RawBrier:F4} delta={row.Delta:+0.0000;-0.0000;0.0000}");
                }
            }
        }
    }
}

static IReadOnlyList<ForecastObservation> SelectPointInTimeForecasts(IEnumerable<ForecastObservation> forecasts)
{
    return forecasts
        .GroupBy(forecast => (
            FixtureKey: BuildMatchLookupKey(
                forecast.FixtureKey,
                forecast.MatchLocalDate,
                forecast.League,
                forecast.HomeTeam,
                forecast.AwayTeam),
            forecast.Market))
        .Select(group =>
        {
            var eligible = group
                .Where(forecast =>
                {
                    var kickoff = ResolveKickoffUtc(
                        forecast.MatchDateTime,
                        forecast.MatchLocalDate,
                        forecast.MatchLocalTime,
                        forecast.Date,
                        forecast.Time);
                    return kickoff == null || forecast.CreatedAt <= kickoff.Value.AddMinutes(5);
                })
                .OrderByDescending(forecast => forecast.CreatedAt)
                .ThenByDescending(forecast => forecast.RevisionNumber)
                .FirstOrDefault();

            return eligible ?? group
                .OrderByDescending(forecast => forecast.CreatedAt)
                .ThenByDescending(forecast => forecast.RevisionNumber)
                .First();
        })
        .ToList();
}

static string BuildMatchLookupKey(
    string? fixtureKey,
    DateOnly? localDate,
    string? league,
    string? homeTeam,
    string? awayTeam)
{
    if (!string.IsNullOrWhiteSpace(fixtureKey))
    {
        return fixtureKey.Trim();
    }

    return string.Join(
        "|",
        localDate?.ToString("yyyy-MM-dd") ?? "unknown-date",
        NormalizeLookupToken(league),
        NormalizeLookupToken(homeTeam),
        NormalizeLookupToken(awayTeam));
}

static string NormalizeLookupToken(string? value)
{
    return (value ?? string.Empty).Trim().ToLowerInvariant();
}

static string NormalizeLeagueLabel(string? league)
{
    return string.IsNullOrWhiteSpace(league) ? "Unknown league" : league.Trim();
}

static string GetConfidenceBandLabel(double calibratedProbability)
{
    return calibratedProbability switch
    {
        >= 0.80 => "80%+",
        >= 0.70 => "70-79%",
        >= 0.60 => "60-69%",
        _ => "<60%"
    };
}

static int GetConfidenceBandSortKey(string bandLabel)
{
    return bandLabel switch
    {
        "<60%" => 0,
        "60-69%" => 1,
        "70-79%" => 2,
        "80%+" => 3,
        _ => 99
    };
}

static DateTime? ResolveKickoffUtc(
    DateTime? storedKickoffUtc,
    DateOnly localDate,
    TimeOnly? localTime,
    string? date,
    string? time)
{
    if (storedKickoffUtc.HasValue)
    {
        return storedKickoffUtc.Value;
    }

    if (localDate != default && localTime.HasValue)
    {
        return DateTimeProvider.ConvertLocalToUtc(localDate.ToDateTime(localTime.Value, DateTimeKind.Unspecified));
    }

    if (string.IsNullOrWhiteSpace(date) || string.IsNullOrWhiteSpace(time))
    {
        return null;
    }

    return DateTimeProvider.ParseCanonicalMatchDateTime(date, time).utcDateTime;
}

static double CalculateProbabilityByMarket(IProbabilityCalculator calculator, MatchData match, PredictionMarket market)
{
    return market switch
    {
        PredictionMarket.BothTeamsScore => calculator.CalculateBttsProbability(match),
        PredictionMarket.Over25Goals => calculator.CalculateOverTwoGoalsProbability(match),
        PredictionMarket.Under25Goals => calculator.CalculateUnderTwoGoalsProbability(match),
        PredictionMarket.Draw => 0.0,
        PredictionMarket.HomeWin => calculator.CalculateHomeWinProbability(match),
        PredictionMarket.AwayWin => calculator.CalculateAwayWinProbability(match),
        _ => 0.0
    };
}

sealed class BenchmarkAccumulator
{
    private double _storedRawSquaredError;
    private double _legacySquaredError;
    private double _modelV2SquaredError;

    public int Count { get; private set; }

    public double StoredRawBrier => Count > 0 ? _storedRawSquaredError / Count : 0.0;
    public double LegacyRawBrier => Count > 0 ? _legacySquaredError / Count : 0.0;
    public double ModelV2RawBrier => Count > 0 ? _modelV2SquaredError / Count : 0.0;

    public void Add(double actual, double storedRaw, double legacyRaw, double modelV2Raw)
    {
        Count++;
        _storedRawSquaredError += Math.Pow(storedRaw - actual, 2);
        _legacySquaredError += Math.Pow(legacyRaw - actual, 2);
        _modelV2SquaredError += Math.Pow(modelV2Raw - actual, 2);
    }
}

sealed class LegacyProbabilityCalculator : IProbabilityCalculator
{
    public MatchProbabilities CalculateProbabilities(MatchData match)
    {
        return new MatchProbabilities(
            Btts: CalculateBttsProbability(match),
            Over25: CalculateOverTwoGoalsProbability(match),
            Under25: CalculateUnderTwoGoalsProbability(match),
            Draw: 0.0,
            HomeWin: CalculateHomeWinProbability(match),
            AwayWin: CalculateAwayWinProbability(match));
    }

    public double CalculateBttsProbability(MatchData match)
    {
        var totalXg = EstimateTotalXg(match);
        if (totalXg <= 0)
            return 0.0;

        var (homeWin, draw, awayWin) = GetNormalizedOneX2(match);
        var (homeXg, awayXg) = ApportionXg(totalXg, homeWin, draw, awayWin);

        var pHomeScores = 1.0 - Math.Exp(-homeXg);
        var pAwayScores = 1.0 - Math.Exp(-awayXg);

        return Math.Clamp(pHomeScores * pAwayScores, 0.0, 1.0);
    }

    public double CalculateOverTwoGoalsProbability(MatchData match)
    {
        if (match.TryGetNormalizedOver25Pair(out var overUnder25))
            return Math.Clamp(overUnder25.over25, 0.0, 1.0);

        if (match.Over25() > 0)
            return Math.Clamp(match.Over25(), 0.0, 1.0);

        var totalXg = EstimateTotalXg(match);
        return totalXg <= 0
            ? 0.0
            : Math.Clamp(PoissonTailProbability(totalXg, 2), 0.0, 1.0);
    }

    public double CalculateUnderTwoGoalsProbability(MatchData match)
    {
        if (match.TryGetNormalizedOver25Pair(out var overUnder25))
            return Math.Clamp(overUnder25.under25, 0.0, 1.0);

        if (match.Under25() > 0)
            return Math.Clamp(match.Under25(), 0.0, 1.0);

        return Math.Clamp(1.0 - CalculateOverTwoGoalsProbability(match), 0.0, 1.0);
    }

    public double CalculateHomeWinProbability(MatchData match)
    {
        var (home, _, _) = GetNormalizedOneX2(match);
        return Math.Clamp(home, 0.0, 1.0);
    }

    public double CalculateAwayWinProbability(MatchData match)
    {
        var (_, _, away) = GetNormalizedOneX2(match);
        return Math.Clamp(away, 0.0, 1.0);
    }

    private static double EstimateTotalXg(MatchData match)
    {
        if (match.TryGetNormalizedOver25Pair(out var overUnder25))
            return InversePoissonOver(overUnder25.over25, threshold: 2);

        if (match.Over25() > 0)
            return InversePoissonOver(match.Over25(), threshold: 2);

        var lambdas = new List<double>();

        if (match.OverOnePointFive > 0)
            lambdas.Add(InversePoissonOver(match.OverOnePointFive, threshold: 1));

        if (match.Over35() > 0)
            lambdas.Add(InversePoissonOver(match.Over35(), threshold: 3));

        return lambdas.Count > 0 ? lambdas.Average() : 0.0;
    }

    private static (double home, double draw, double away) GetNormalizedOneX2(MatchData match)
    {
        if (match.TryGetNormalizedOneX2(out var normalized))
            return normalized;

        var total = Math.Max(match.HomeWin + match.Draw + match.AwayWin, 0.0);
        if (total > 0)
            return (match.HomeWin / total, match.Draw / total, match.AwayWin / total);

        return (0.0, 0.0, 0.0);
    }

    private static (double homeXg, double awayXg) ApportionXg(double totalXg, double homeWin, double draw, double awayWin)
    {
        var homeStrength = homeWin + (draw * 0.5);
        var awayStrength = awayWin + (draw * 0.5);
        var totalStrength = homeStrength + awayStrength;

        if (totalStrength <= 0)
            return (totalXg * 0.5, totalXg * 0.5);

        var homeShare = homeStrength / totalStrength;
        var awayShare = awayStrength / totalStrength;
        return (totalXg * homeShare, totalXg * awayShare);
    }

    private static double PoissonTailProbability(double lambda, int threshold)
    {
        var cdf = 0.0;
        for (var k = 0; k <= threshold; k++)
            cdf += PoissonProb(k, lambda);

        return 1.0 - cdf;
    }

    private static double InversePoissonOver(double targetProb, int threshold)
    {
        if (targetProb <= 0)
            return 0.0;

        var targetCdf = Math.Clamp(1.0 - targetProb, 0.01, 0.99);
        var lambda = Math.Max(threshold + 1.0 - targetCdf * (threshold + 1.0), 0.5);

        for (var i = 0; i < 20; i++)
        {
            var cdf = 0.0;
            for (var k = 0; k <= threshold; k++)
                cdf += PoissonProb(k, lambda);

            var error = cdf - targetCdf;
            if (Math.Abs(error) < 1e-6)
                break;

            var derivative = -PoissonProb(threshold, lambda);
            if (Math.Abs(derivative) < 1e-12)
                break;

            lambda = Math.Clamp(lambda - (error / derivative), 0.1, 8.0);
        }

        return Math.Clamp(lambda, 0.1, 8.0);
    }

    private static double PoissonProb(int k, double lambda)
    {
        if (lambda <= 0)
            return k == 0 ? 1.0 : 0.0;

        double factorial = 1.0;
        for (var i = 2; i <= k; i++)
            factorial *= i;

        return Math.Exp(-lambda) * Math.Pow(lambda, k) / factorial;
    }
}

enum RunnerMode
{
    Sync,
    Score,
    ScoreBackfill,
    Benchmark,
    ExportHistory,
    ImportHistory
}

sealed class NoOpExtractFromExcel : IExtractFromExcel
{
    public IEnumerable<MatchData> ExtractMatchDatasetFromFile(DateTime? targetLocalDate = null) => [];
}
