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
    "--analysis" or "analysis" or "--daily-analysis" or "daily-analysis" => RunnerMode.Analysis,
    "--full" or "full" => RunnerMode.Full,
    "--refresh" or "refresh" => RunnerMode.Refresh,
    "--score-backfill" or "score-backfill" => RunnerMode.ScoreBackfill,
    "--score" or "score" => RunnerMode.Score,
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
services.AddScoped<IDataAnalyzerService, DataAnalyzerService>();
services.AddScoped<IWebScraperService, WebScraperService>();
if (mode is RunnerMode.Score or RunnerMode.ScoreBackfill or RunnerMode.Analysis)
{
    // These modes operate on already-persisted data and do not need workbook access.
    services.AddScoped<IExtractFromExcel, NoOpExtractFromExcel>();
}
else
{
    services.AddScoped<IExtractFromExcel, ExtractFromExcel>();
}
services.AddScoped<IProbabilityCalculator, ProbabilityCalculator>();
services.AddScoped<ICalibrationService, CalibrationService>();
services.AddScoped<IThresholdTuningService, ThresholdTuningService>();
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
IAnalyzerService analyzer;

Console.WriteLine($"Mode: {mode}");

switch (mode)
{
    case RunnerMode.Analysis:
        analyzer = scope.ServiceProvider.GetRequiredService<IAnalyzerService>();
        Console.WriteLine("Before:");
        await PrintMissingSummaryAsync(context);
        await PrintBttsSummaryAsync(context);
        await analyzer.RunDailyAnalysisAsync();
        break;
    case RunnerMode.Full:
        analyzer = scope.ServiceProvider.GetRequiredService<IAnalyzerService>();
        Console.WriteLine("Before:");
        await PrintMissingSummaryAsync(context);
        await PrintBttsSummaryAsync(context);
        await analyzer.ExtractDataAndSyncDatabaseAsync();
        await analyzer.GeneratePredictionsAsync();
        await analyzer.RunScoreUpdaterAsync();
        await analyzer.RunScoreUpdaterAsync(14, "backfill");
        await analyzer.RunDailyAnalysisAsync();
        break;
    case RunnerMode.Refresh:
        analyzer = scope.ServiceProvider.GetRequiredService<IAnalyzerService>();
        Console.WriteLine("Before:");
        await PrintMissingSummaryAsync(context);
        await PrintBttsSummaryAsync(context);
        await analyzer.ExtractDataAndSyncDatabaseAsync();
        await analyzer.GeneratePredictionsAsync();
        break;
    case RunnerMode.Score:
        analyzer = scope.ServiceProvider.GetRequiredService<IAnalyzerService>();
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

    var winnerPriced = todayMatches.Count(match => match.HomeWin > 0 && match.AwayWin > 0);
    var setsPriced = todayMatches.Count(match => match.OverTwoPointFiveSets > 0 && match.UnderTwoPointFiveSets > 0);
    var handicapPriced = todayMatches.Count(match => match.SetHandicapHome > 0 && match.SetHandicapAway > 0);

    Console.WriteLine(
        $"Source pricing for {today}: totalMatches={todayMatches.Count}, winnerPairs={winnerPriced}, totalSetsPairs={setsPriced}, handicapPairs={handicapPriced}");

    foreach (var sample in todayMatches
                 .Where(match => match.HomeWin > 0 && match.AwayWin > 0)
                 .OrderByDescending(match => Math.Max(match.HomeWin, match.AwayWin))
                 .Take(5))
    {
        Console.WriteLine(
            $"  {sample.HomeTeam} vs {sample.AwayTeam} [{sample.League}] -> home={sample.HomeWin:F3}, away={sample.AwayWin:F3}, over2.5={sample.OverTwoPointFiveSets:F3}, under2.5={sample.UnderTwoPointFiveSets:F3}");
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

enum RunnerMode
{
    Analysis,
    Full,
    Refresh,
    Sync,
    Score,
    ScoreBackfill
}

sealed class NoOpExtractFromExcel : IExtractFromExcel
{
    public IEnumerable<MatchData> ExtractMatchDatasetFromFile(DateTime? targetLocalDate = null) => [];
}
