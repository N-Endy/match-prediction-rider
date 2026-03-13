using MatchPredictor.Application.Services;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

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

var loggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        })
        .SetMinimumLevel(LogLevel.Information);
});

var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
    .UseNpgsql(connectionString)
    .Options;

await using var context = new ApplicationDbContext(dbOptions);

Console.WriteLine("Before:");
await PrintMissingSummaryAsync(context);

var scraperLogger = loggerFactory.CreateLogger<WebScraperService>();
var analyzerLogger = loggerFactory.CreateLogger<AnalyzerService>();
var predictionSettings = configuration.GetSection("PredictionSettings").Get<PredictionSettings>() ?? new PredictionSettings();

var analyzer = new AnalyzerService(
    new NoOpDataAnalyzerService(),
    new WebScraperService(configuration, scraperLogger),
    context,
    new NoOpExtractFromExcel(),
    new NoOpRegressionPredictorService(),
    new NoOpCalibrationService(),
    new NoOpThresholdTuningService(),
    Options.Create(predictionSettings),
    analyzerLogger);

await analyzer.RunScoreUpdaterAsync();

context.ChangeTracker.Clear();

Console.WriteLine();
Console.WriteLine("After:");
await PrintMissingSummaryAsync(context);

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

internal sealed class NoOpDataAnalyzerService : IDataAnalyzerService
{
    public IReadOnlyList<PredictionCandidate> BuildForecastCandidates(IEnumerable<MatchData> matches) => [];
    public IReadOnlyList<PredictionCandidate> SelectPublishedPredictions(IEnumerable<PredictionCandidate> forecastCandidates) => [];
    public IReadOnlyList<PredictionCandidate> BothTeamsScore(IEnumerable<MatchData> matches) => [];
    public IReadOnlyList<PredictionCandidate> OverTwoGoals(IEnumerable<MatchData> matches) => [];
    public IReadOnlyList<PredictionCandidate> Draw(IEnumerable<MatchData> matches) => [];
    public IReadOnlyList<PredictionCandidate> StraightWin(IEnumerable<MatchData> matches) => [];
}

internal sealed class NoOpExtractFromExcel : IExtractFromExcel
{
    public IEnumerable<MatchData> ExtractMatchDatasetFromFile() => [];
}

internal sealed class NoOpRegressionPredictorService : IRegressionPredictorService
{
    public IEnumerable<RegressionPrediction> GeneratePredictions(IEnumerable<MatchData> upcomingMatches) => [];
}

internal sealed class NoOpCalibrationService : ICalibrationService
{
    public double Calibrate(PredictionMarket market, double rawProbability) => rawProbability;

    public CalibrationDecision CalibrateWithDecision(PredictionMarket market, double rawProbability) =>
        new()
        {
            Probability = rawProbability,
            CalibratorUsed = "Bucket"
        };

    public Task RebuildProfilesAsync() => Task.CompletedTask;
}

internal sealed class NoOpThresholdTuningService : IThresholdTuningService
{
    public double GetThreshold(PredictionMarket market, double fallbackThreshold) => fallbackThreshold;

    public ThresholdDecision GetThresholdDecision(PredictionMarket market, double fallbackThreshold) =>
        new()
        {
            Threshold = fallbackThreshold,
            ThresholdSource = "Configured"
        };

    public Task RebuildProfilesAsync() => Task.CompletedTask;
}
