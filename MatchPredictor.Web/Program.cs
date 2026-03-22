using System.Data;
using Hangfire;
using Hangfire.Common;
using Hangfire.PostgreSql;
using MatchPredictor.Application.Services;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Infrastructure;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Repositories;
using MatchPredictor.Infrastructure.Services;
using MatchPredictor.Infrastructure.Utils;
using MatchPredictor.Web.Configuration;
using MatchPredictor.Web.Extensions;
using MatchPredictor.Web.Filters;
using MatchPredictor.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AddPageRoute("/Health/Health", "/ops/health");
    options.Conventions.AddPageRoute("/HealthLegacy", "/Health/Health");
    options.Conventions.AddPageRoute("/HealthLegacy", "/ops");
});
builder.Services.AddControllers();
builder.Services.AddHealthChecks();
builder.Services.AddHttpClient();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<OperationalStartupState>();
builder.Services.AddHttpClientServices();
builder.Services.AddRedisMemoryCache(builder.Configuration);
builder.Services.AddMatchPredictorRateLimiting();

var runtimeMode = RuntimeModeOptions.FromConfiguration(builder.Configuration);
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddScoped<IMatchDataRepository, MatchDataRepository>();
builder.Services.AddScoped<IPredictionQueries, PredictionQueries>();
builder.Services.AddScoped<IDataAnalyzerService, DataAnalyzerService>();
builder.Services.AddScoped<IWebScraperService, WebScraperService>();
builder.Services.AddScoped<IExtractFromExcel, ExtractFromExcel>();
builder.Services.AddScoped<IProbabilityCalculator, ProbabilityCalculator>();
builder.Services.AddScoped<ICalibrationService, CalibrationService>();
builder.Services.AddScoped<IThresholdTuningService, ThresholdTuningService>();
builder.Services.AddScoped<IForecastEvaluationService, ForecastEvaluationService>();
builder.Services.AddScoped<IAnalyzerService, AnalyzerService>();
builder.Services.AddSingleton<AiScoreSourceHealthTracker>();
builder.Services.AddSingleton<SofaScoreSourceHealthTracker>();
builder.Services.AddScoped<SportyBetBookingService>();
builder.Services.AddScoped<ISportyBetBookingService>(provider => provider.GetRequiredService<SportyBetBookingService>());
builder.Services.AddScoped<ISourceMarketPricingService>(provider => provider.GetRequiredService<SportyBetBookingService>());
builder.Services.AddScoped<IValueBetsService, ValueBetsService>();

builder.Services.AddDataProtection()
    .PersistKeysToDbContext<ApplicationDbContext>();

builder.Host.UseSerilog((context, services, configuration) =>
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

builder.Services.Configure<MatchPredictor.Domain.Models.PredictionSettings>(
    builder.Configuration.GetSection("PredictionSettings"));

builder.Services.AddLogging();
builder.Services.AddSingleton<LogFailureAttribute>();
builder.Services.AddSingleton<IJobFilterProvider, DependencyInjectionFilterProvider>();

builder.Services.AddHangfire((_, config) =>
{
    config.UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(
            bootstrapperOptions => bootstrapperOptions.UseNpgsqlConnection(connectionString),
            new PostgreSqlStorageOptions
            {
                SchemaName = "hangfire",
                QueuePollInterval = TimeSpan.FromSeconds(15),
                PrepareSchemaIfNecessary = true,
                DistributedLockTimeout = TimeSpan.FromMinutes(1),
                TransactionSynchronisationTimeout = TimeSpan.FromMinutes(1)
            });

    config.UseFilter(new AutomaticRetryAttribute { Attempts = 3 });
});

var hangfireWorkerCount = ResolveHangfireWorkerCount(builder.Configuration);
if (runtimeMode.RunBackgroundJobs)
{
    builder.Services.AddHangfireServer(options =>
    {
        options.WorkerCount = hangfireWorkerCount;
    });
}

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.ListenAnyIP(ResolveHttpPort());
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var logger = services.GetRequiredService<ILogger<Program>>();
    var startupState = services.GetRequiredService<OperationalStartupState>();
    startupState.ConfigureRuntimeMode(runtimeMode.RunBackgroundJobs, runtimeMode.BrowserScrapingEnabled);

    try
    {
        logger.LogInformation(
            "Runtime mode: background jobs {BackgroundJobsState}; browser scraping {BrowserScrapingState}.",
            runtimeMode.RunBackgroundJobs ? "enabled" : "disabled",
            runtimeMode.BrowserScrapingEnabled ? "enabled" : "disabled");

        if (runtimeMode.RunBackgroundJobs && !runtimeMode.BrowserScrapingEnabled)
        {
            logger.LogWarning(
                "Background jobs are enabled while browser scraping is disabled. Tennis result jobs that require browser scraping may fail until ENABLE_BROWSER_SCRAPING is turned on.");
        }

        var context = services.GetRequiredService<ApplicationDbContext>();
        if (!await HasEfMigrationsHistoryAsync(context))
        {
            logger.LogInformation(
                "No EF migration history table detected. Ensuring the tennis schema is created directly from the current model.");

            await context.Database.EnsureCreatedAsync();
            await RepairLegacyEnsureCreatedSchemaAsync(context, logger);
            await StampKnownMigrationsAsync(context, logger);
        }
        else
        {
            try
            {
                await context.Database.MigrateAsync();
            }
            catch (InvalidOperationException ex) when (ContainsPendingModelChangesWarning(ex))
            {
                logger.LogWarning(
                    ex,
                    "Pending EF model changes detected while bootstrapping the tennis database. Falling back to EnsureCreated for this fresh standalone deployment.");

                await context.Database.EnsureCreatedAsync();
                await RepairLegacyEnsureCreatedSchemaAsync(context, logger);
                await StampKnownMigrationsAsync(context, logger);
            }
        }

        startupState.MarkDatabaseInitialized();
        logger.LogInformation("TennisPredictor database initialized successfully.");

        var storage = services.GetRequiredService<JobStorage>();
        var stats = storage.GetMonitoringApi().GetStatistics();
        startupState.MarkHangfireInitialized();
        logger.LogInformation("Hangfire initialized. Servers: {Servers}; recurring jobs: {RecurringJobs}.", stats.Servers, stats.Recurring);
    }
    catch (Exception ex)
    {
        startupState.MarkInitializationFailed(ex.Message);
        logger.LogCritical(ex, "Startup aborted because database or Hangfire initialization failed.");
        throw new InvalidOperationException("Application startup aborted because database initialization failed.", ex);
    }
}

if (runtimeMode.RunBackgroundJobs)
{
    using var scope = app.Services.CreateScope();
    var recurringJobs = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var startupState = scope.ServiceProvider.GetRequiredService<OperationalStartupState>();

    try
    {
        var watTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Lagos");

        recurringJobs.RemoveIfExists("daily-prediction-job");
        recurringJobs.RemoveIfExists("prediction-generation-job-noon");
        recurringJobs.RemoveIfExists("prediction-prewarm-job");
        recurringJobs.RemoveIfExists("prediction-generation-post-analysis-job");
        recurringJobs.RemoveIfExists("prediction-generation-refresh-job");
        recurringJobs.RemoveIfExists("score-update-job");
        recurringJobs.RemoveIfExists("score-backfill-job");
        recurringJobs.RemoveIfExists("closing-line-snapshot-job");

        recurringJobs.AddOrUpdate<IAnalyzerService>(
            "prediction-prewarm-job",
            service => service.ExtractDataAndSyncDatabaseAsync(1, "prewarm"),
            "40 23 * * *",
            new RecurringJobOptions { TimeZone = watTimeZone });

        recurringJobs.AddOrUpdate<IAnalyzerService>(
            "prediction-generation-job",
            service => service.ExtractDataAndSyncDatabaseAsync(0, "scheduled-sync"),
            "35 0 * * *",
            new RecurringJobOptions { TimeZone = watTimeZone });

        recurringJobs.AddOrUpdate<IAnalyzerService>(
            "prediction-generation-post-analysis-job",
            service => service.ExtractDataAndSyncDatabaseAsync(0, "morning-refresh"),
            "30 4 * * *",
            new RecurringJobOptions { TimeZone = watTimeZone });

        recurringJobs.AddOrUpdate<IAnalyzerService>(
            "prediction-generation-refresh-job",
            service => service.ExtractDataAndSyncDatabaseAsync(0, "day-refresh"),
            "30 12,16 * * *",
            new RecurringJobOptions { TimeZone = watTimeZone });

        recurringJobs.AddOrUpdate<IAnalyzerService>(
            "score-update-job",
            service => service.RunScoreUpdaterAsync(1, "recent"),
            "*/6 * * * *",
            new RecurringJobOptions { TimeZone = watTimeZone });

        recurringJobs.AddOrUpdate<IAnalyzerService>(
            "score-backfill-job",
            service => service.RunScoreUpdaterAsync(14, "backfill"),
            "17 * * * *",
            new RecurringJobOptions { TimeZone = watTimeZone });

        recurringJobs.AddOrUpdate<IAnalyzerService>(
            "closing-line-snapshot-job",
            service => service.CaptureClosingLineSnapshotsAsync(15),
            "*/5 * * * *",
            new RecurringJobOptions { TimeZone = watTimeZone });

        recurringJobs.AddOrUpdate<IAnalyzerService>(
            "daily-analysis-job",
            service => service.RunDailyAnalysisAsync(),
            "20 0 * * *",
            new RecurringJobOptions { TimeZone = watTimeZone });

        recurringJobs.AddOrUpdate<IAnalyzerService>(
            "cleanup-old-predictions",
            service => service.CleanupOldPredictionsAndMatchDataAsync(),
            "0 1 * * *",
            new RecurringJobOptions { TimeZone = watTimeZone });

        startupState.MarkRecurringJobsRegistered();
        logger.LogInformation("Tennis recurring jobs registered successfully.");
    }
    catch (Exception ex)
    {
        startupState.MarkInitializationFailed(ex.Message);
        logger.LogCritical(ex, "Startup aborted because recurring Hangfire job registration failed.");
        throw;
    }
}
else
{
    using var scope = app.Services.CreateScope();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Skipping recurring Hangfire job registration because RUN_BACKGROUND_JOBS is disabled.");
}

if (runtimeMode.RunBackgroundJobs)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        var today = DateTimeProvider.GetLocalDate();
        var hasTodayPredictions = await db.Predictions.AnyAsync(prediction =>
            prediction.MatchLocalDate == today && prediction.IsCurrentRevision);

        if (!hasTodayPredictions)
        {
            logger.LogInformation("No tennis predictions found for today. Queuing initial tennis extraction and prediction generation.");
            var backgroundJobs = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>();
            backgroundJobs.Enqueue<IAnalyzerService>(service => service.ExtractDataAndSyncDatabaseAsync());
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Could not check for existing tennis predictions or trigger initial jobs.");
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthorization();

app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    DashboardTitle = "Tennis Predictor Jobs",
    StatsPollingInterval = 5000,
    Authorization = app.Environment.IsDevelopment()
        ? new Hangfire.Dashboard.IDashboardAuthorizationFilter[] { new HangfireAllowAllFilter() }
        : new Hangfire.Dashboard.IDashboardAuthorizationFilter[]
        {
            new HangfireBasicAuthFilter(
                builder.Configuration["Hangfire:Username"],
                builder.Configuration["Hangfire:Password"])
        }
});

app.MapRazorPages();
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

static int ResolveHangfireWorkerCount(IConfiguration configuration)
{
    var configuredWorkerCount = configuration.GetValue<int?>("Hangfire:WorkerCount");
    if (configuredWorkerCount is > 0)
    {
        return configuredWorkerCount.Value;
    }

    var runningOnRailway =
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RAILWAY_ENVIRONMENT")) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RAILWAY_PROJECT_ID")) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RAILWAY_SERVICE_ID"));

    if (runningOnRailway)
    {
        return 1;
    }

    return Math.Max(1, Math.Min(2, Environment.ProcessorCount));
}

static int ResolveHttpPort()
{
    var explicitPort = Environment.GetEnvironmentVariable("PORT");
    if (int.TryParse(explicitPort, out var parsedPort) && parsedPort > 0)
    {
        return parsedPort;
    }

    var httpPorts = Environment.GetEnvironmentVariable("HTTP_PORTS");
    if (!string.IsNullOrWhiteSpace(httpPorts))
    {
        var firstPort = httpPorts
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (int.TryParse(firstPort, out parsedPort) && parsedPort > 0)
        {
            return parsedPort;
        }
    }

    return 10000;
}

static async Task<bool> HasEfMigrationsHistoryAsync(ApplicationDbContext context)
{
    var connection = context.Database.GetDbConnection();
    var shouldCloseConnection = connection.State != ConnectionState.Open;
    if (shouldCloseConnection)
    {
        await connection.OpenAsync();
    }

    try
    {
        await using var tableExistsCommand = connection.CreateCommand();
        tableExistsCommand.CommandText = """
            select exists (
                select 1
                from information_schema.tables
                where table_schema = current_schema()
                  and table_name = '__EFMigrationsHistory'
            );
            """;

        var tableExistsResult = await tableExistsCommand.ExecuteScalarAsync();
        var tableExists = tableExistsResult is bool exists && exists;
        if (!tableExists)
        {
            return false;
        }

        await using var rowExistsCommand = connection.CreateCommand();
        rowExistsCommand.CommandText = """select exists (select 1 from "__EFMigrationsHistory");""";
        var rowExistsResult = await rowExistsCommand.ExecuteScalarAsync();
        return rowExistsResult is bool rowExists && rowExists;
    }
    catch (PostgresException ex) when (string.Equals(ex.SqlState, PostgresErrorCodes.UndefinedTable, StringComparison.Ordinal))
    {
        return false;
    }
    finally
    {
        if (shouldCloseConnection)
        {
            await connection.CloseAsync();
        }
    }
}

static async Task RepairLegacyEnsureCreatedSchemaAsync(
    ApplicationDbContext context,
    Microsoft.Extensions.Logging.ILogger<Program> logger)
{
    var statements = new[]
    {
        """alter table if exists "ScrapingLogs" add column if not exists "PayloadJson" text;""",
        """alter table if exists "ScrapingLogs" add column if not exists "PredictionRunId" uuid;""",
        """alter table if exists "ScrapingLogs" add column if not exists "RunKind" character varying(64);""",
        """alter table if exists "ScrapingLogs" add column if not exists "RunLabel" character varying(128);""",
        """alter table if exists "ScrapingLogs" add column if not exists "SourceName" character varying(64);""",
        """alter table if exists "ScrapingLogs" add column if not exists "Stage" character varying(64);""",
        """create index if not exists "IX_ScrapingLogs_PredictionRunId" on "ScrapingLogs" ("PredictionRunId");""",
        """create index if not exists "IX_ScrapingLogs_RunKind_RunLabel_Timestamp" on "ScrapingLogs" ("RunKind", "RunLabel", "Timestamp");""",
        """create index if not exists "IX_ScrapingLogs_SourceName_Stage_Timestamp" on "ScrapingLogs" ("SourceName", "Stage", "Timestamp");""",

        """alter table if exists "MatchScores" add column if not exists "AwaySetsWon" integer;""",
        """alter table if exists "MatchScores" add column if not exists "HomeSetsWon" integer;""",
        """alter table if exists "MatchScores" add column if not exists "NormalizedScoreline" text;""",

        """alter table if exists "MatchDatas" add column if not exists "AwaySetsWon" integer;""",
        """alter table if exists "MatchDatas" add column if not exists "HomeSetsWon" integer;""",
        """alter table if exists "MatchDatas" add column if not exists "NormalizedScoreline" text;""",
        """alter table if exists "MatchDatas" add column if not exists "SetHandicapLabel" text;""",
        """alter table if exists "MatchDatas" add column if not exists "SourceMatchId" text;""",
        """alter table if exists "MatchDatas" add column if not exists "Surface" text;""",
        """alter table if exists "MatchDatas" add column if not exists "Tournament" text;""",

        """alter table if exists "AiScoreMatchScores" add column if not exists "AwaySetsWon" integer;""",
        """alter table if exists "AiScoreMatchScores" add column if not exists "HomeSetsWon" integer;""",
        """alter table if exists "AiScoreMatchScores" add column if not exists "NormalizedScoreline" text;""",

        """
        create table if not exists "SofaScoreMatchScores"
        (
            "Id" integer generated by default as identity primary key,
            "League" text not null,
            "HomeTeam" text not null,
            "AwayTeam" text not null,
            "Score" text not null,
            "NormalizedScoreline" text null,
            "HomeSetsWon" integer null,
            "AwaySetsWon" integer null,
            "DisplayedScore" text null,
            "RegularTimeScore" text null,
            "HalfTimeScore" text null,
            "ExtraTimeScore" text null,
            "StatusText" text null,
            "EventUrl" text not null,
            "MatchTime" timestamp with time zone not null,
            "IsLive" boolean not null
        );
        """,
        """alter table if exists "SofaScoreMatchScores" add column if not exists "NormalizedScoreline" text;""",
        """alter table if exists "SofaScoreMatchScores" add column if not exists "HomeSetsWon" integer;""",
        """alter table if exists "SofaScoreMatchScores" add column if not exists "AwaySetsWon" integer;""",
        """alter table if exists "SofaScoreMatchScores" add column if not exists "DisplayedScore" text;""",
        """alter table if exists "SofaScoreMatchScores" add column if not exists "RegularTimeScore" text;""",
        """alter table if exists "SofaScoreMatchScores" add column if not exists "HalfTimeScore" text;""",
        """alter table if exists "SofaScoreMatchScores" add column if not exists "ExtraTimeScore" text;""",
        """alter table if exists "SofaScoreMatchScores" add column if not exists "StatusText" text;""",
        """alter table if exists "SofaScoreMatchScores" add column if not exists "EventUrl" text;""",
        """create index if not exists "IX_SofaScoreMatchScores_EventUrl" on "SofaScoreMatchScores" ("EventUrl");""",
        """create index if not exists "IX_SofaScoreMatchScores_MatchTime_HomeTeam_AwayTeam" on "SofaScoreMatchScores" ("MatchTime", "HomeTeam", "AwayTeam");""",
        """create index if not exists "IX_SofaScoreMatchScores_MatchTime_IsLive" on "SofaScoreMatchScores" ("MatchTime", "IsLive");"""
    };

    foreach (var statement in statements)
    {
        await context.Database.ExecuteSqlRawAsync(statement);
    }

    logger.LogInformation("Legacy EnsureCreated tennis schema repair completed.");
}

static async Task StampKnownMigrationsAsync(
    ApplicationDbContext context,
    Microsoft.Extensions.Logging.ILogger<Program> logger)
{
    var migrationsAssembly = context.GetService<IMigrationsAssembly>();
    var knownMigrations = migrationsAssembly.Migrations.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    if (knownMigrations.Length == 0)
    {
        return;
    }

    await context.Database.ExecuteSqlRawAsync(
        """
        create table if not exists "__EFMigrationsHistory"
        (
            "MigrationId" character varying(150) not null,
            "ProductVersion" character varying(32) not null,
            constraint "PK___EFMigrationsHistory" primary key ("MigrationId")
        );
        """);

    var productVersion = typeof(Migration).Assembly.GetName().Version?.ToString(3) ?? "10.0.0";
    foreach (var migrationId in knownMigrations)
    {
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            insert into "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            values ({migrationId}, {productVersion})
            on conflict ("MigrationId") do nothing;
            """);
    }

    logger.LogInformation(
        "Stamped EF migration history for legacy tennis database with {MigrationCount} known migrations.",
        knownMigrations.Length);
}

static bool ContainsPendingModelChangesWarning(Exception exception)
{
    return exception.Message.Contains("PendingModelChangesWarning", StringComparison.Ordinal) ||
           (exception.InnerException is not null && ContainsPendingModelChangesWarning(exception.InnerException));
}
