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
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Polly;
using Polly.Extensions.Http;
using MatchPredictor.Web.Filters;
using MatchPredictor.Web.Middleware;
using MatchPredictor.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddRazorPages();
builder.Services.AddHealthChecks();
builder.Services.AddHttpClient();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<OperationalStartupState>();

builder.Services.AddHttpClientServices();
builder.Services.AddRedisMemoryCache(builder.Configuration);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

var runtimeMode = RuntimeModeOptions.FromConfiguration(builder.Configuration);

// Configure database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

// Register application services
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IMatchDataRepository, MatchDataRepository>();
builder.Services.AddScoped<PredictionQueries>();
builder.Services.AddScoped<IPredictionQueries>(provider => new CachedPredictionQueries(
    provider.GetRequiredService<PredictionQueries>(),
    provider.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()));
builder.Services.AddScoped<IHealthQueryService, HealthQueryService>();
        builder.Services.AddScoped<IDataAnalyzerService, DataAnalyzerService>();
builder.Services.AddScoped<IWebScraperService, WebScraperService>();
builder.Services.AddScoped<IExtractFromExcel, ExtractFromExcel>();
builder.Services.AddScoped<IProbabilityCalculator, ProbabilityCalculator>();
builder.Services.AddScoped<ICalibrationService, CalibrationService>();
builder.Services.AddScoped<IProbabilityCorrectionService, ProbabilityCorrectionService>();
builder.Services.AddScoped<IThresholdTuningService, ThresholdTuningService>();
builder.Services.AddScoped<ILearningLoopService, LearningLoopService>();
builder.Services.AddScoped<IForecastEvaluationService, ForecastEvaluationService>();
builder.Services.AddScoped<IAnalyzerService, AnalyzerService>();
builder.Services.AddScoped<IRegressionPredictorService, RegressionPredictorService>();
builder.Services.AddSingleton<AiScoreSourceHealthTracker>();
builder.Services.AddSingleton<SofaScoreSourceHealthTracker>();
builder.Services.AddScoped<SportyBetBookingService>();
builder.Services.AddScoped<ISportyBetBookingService>(provider => provider.GetRequiredService<SportyBetBookingService>());
builder.Services.AddScoped<ISourceMarketPricingService>(provider => provider.GetRequiredService<SportyBetBookingService>());
builder.Services.AddScoped<AiChatKnowledgeService>();
builder.Services.AddScoped<IAiChatSchemaFallbackService, AiChatSchemaFallbackService>();
builder.Services.AddScoped<AiChatRequestParser>();
builder.Services.AddScoped<IAiChatFootballInsightService, AiChatFootballInsightService>();
builder.Services.AddScoped<IAiAdvisorService, AiAdvisorService>();
builder.Services.AddScoped<IValueBetsService, ValueBetsService>();
builder.Services.AddScoped<IUserTrackingService, UserTrackingService>();
builder.Services.AddScoped<IAiChatAuthTicketService, AiChatAuthTicketService>();

// Controllers for API endpoints (booking, AI chat)
builder.Services.AddControllers();
builder.Services.AddMatchPredictorRateLimiting();

// Behind a TLS-terminating proxy (Render/containers) the scheme and client IP arrive
// via X-Forwarded-* headers; without this, IsHttps, HSTS, and rate-limit keys are wrong.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // The hosting platform's proxy addresses are not statically known, so clear the
    // defaults and accept the header from the immediate upstream hop only.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    options.ForwardLimit = 1;
});

// Configure data protection
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<ApplicationDbContext>();

// Configure logging
builder.Host.UseSerilog((context, services, configuration) =>
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
);

// Register configuration settings
builder.Services.Configure<MatchPredictor.Domain.Models.PredictionSettings>(
    builder.Configuration.GetSection("PredictionSettings"));

// Configure Hangfire
builder.Services.AddLogging();
builder.Services.AddSingleton<LogFailureAttribute>();
builder.Services.AddSingleton<IJobFilterProvider, DependencyInjectionFilterProvider>();

builder.Services.AddHangfire((_, config) =>
{
    config.UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(
            // ✅ NEW: Wrap the connection string in the bootstrapper action
            c => c.UseNpgsqlConnection(connectionString), 

            // KEEP: Your options remain exactly the same
            new PostgreSqlStorageOptions
            {
                SchemaName = "hangfire",
                QueuePollInterval = TimeSpan.FromSeconds(15),
                PrepareSchemaIfNecessary = true, 
                DistributedLockTimeout = TimeSpan.FromMinutes(1),
                TransactionSynchronisationTimeout = TimeSpan.FromMinutes(1)
            }
        );

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

// Configure Kestrel
var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.ListenAnyIP(int.Parse(port));
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var logger = services.GetRequiredService<ILogger<Program>>();
    var startupState = services.GetRequiredService<OperationalStartupState>();
    startupState.ConfigureRuntimeMode(
        runtimeMode.RunBackgroundJobs,
        runtimeMode.BrowserScrapingEnabled,
        runtimeMode.UseExternalCron);
    
    try
    {
        logger.LogInformation(
            "Runtime mode: background jobs {BackgroundJobsState}; browser scraping {BrowserScrapingState}; user tracking {UserTrackingState}; external cron {ExternalCronState}.",
            runtimeMode.RunBackgroundJobs ? "enabled" : "disabled",
            runtimeMode.BrowserScrapingEnabled ? "enabled" : "disabled",
            runtimeMode.UserTrackingEnabled ? "enabled" : "disabled",
            runtimeMode.UseExternalCron ? "enabled" : "disabled");
        if (runtimeMode.RunBackgroundJobs && !runtimeMode.BrowserScrapingEnabled)
        {
            logger.LogWarning(
                "Background jobs are enabled while browser scraping is disabled. Jobs that require Chrome-based scraping will fail until ENABLE_BROWSER_SCRAPING is turned on.");
        }

        // Step 1: Migrate application database
        var context = services.GetRequiredService<ApplicationDbContext>();
        await context.Database.MigrateAsync();
        logger.LogInformation("Database initialized successfully.");
        startupState.MarkDatabaseInitialized();
        
        // Step 2: Initialize Hangfire storage
        var storage = services.GetRequiredService<JobStorage>();
        
        // Force Hangfire to create its tables by accessing monitoring the API
        var monitoringApi = storage.GetMonitoringApi();
        var stats = monitoringApi.GetStatistics();
        
        logger.LogInformation("✅ Hangfire initialized - Servers: {StatsServers}, Jobs: {StatsRecurring}", stats.Servers, stats.Recurring);
        if (runtimeMode.RunBackgroundJobs)
        {
            logger.LogInformation("Configured Hangfire worker count: {WorkerCount}.", hangfireWorkerCount);
        }
        else
        {
            logger.LogInformation("Hangfire server startup is disabled for this service.");
        }
        startupState.MarkHangfireInitialized();
    }
    catch (Exception ex)
    {
        startupState.MarkInitializationFailed(ex.Message);
        logger.LogCritical(ex, "❌ Startup aborted because database or Hangfire initialization failed.");
        throw new InvalidOperationException("Application startup aborted because database initialization failed.", ex);
    }
}

// Register recurring Hangfire jobs only on the worker service (unless external cron is enabled)
if (runtimeMode.RunBackgroundJobs)
{
    using var scope = app.Services.CreateScope();
    var recurringJobs = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var startupState = scope.ServiceProvider.GetRequiredService<OperationalStartupState>();

    try
    {
        HangfireRecurringJobs.RemoveAll(recurringJobs);

        if (runtimeMode.UseExternalCron)
        {
            startupState.MarkExternalCronEnabled();
            logger.LogInformation(
                "External cron mode enabled (USE_EXTERNAL_CRON=true). Hangfire recurring jobs cleared; schedule jobs via cron-job.org HTTP triggers.");
        }
        else
        {
            HangfireRecurringJobs.Register(recurringJobs);
            startupState.MarkRecurringJobsRegistered();
            logger.LogInformation("Recurring jobs registered successfully (WAT timezone).");
        }
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

// Auto-trigger initial data scraping only on the worker service
if (runtimeMode.RunBackgroundJobs)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    
    try
    {
        var today = DateOnly.FromDateTime(DateTimeProvider.GetLocalTime());
        var hasTodayPredictions = await db.Predictions.AnyAsync(p => p.MatchLocalDate == today && p.IsCurrentRevision);
        
        if (!hasTodayPredictions)
        {
            logger.LogInformation("No predictions found for today. Queuing daily analysis followed by initial data scraping...");
            var backgroundJobs = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>();
            var analysisJobId = backgroundJobs.Enqueue<IAnalyzerService>(service => service.RunDailyAnalysisAsync());
            backgroundJobs.ContinueJobWith<IAnalyzerService>(
                analysisJobId,
                service => service.ExtractDataAndSyncDatabaseAsync(),
                JobContinuationOptions.OnlyOnSucceededState);
            logger.LogInformation("✅ Initial daily analysis and scraping jobs queued successfully.");
        }
        else
        {
            logger.LogInformation("Predictions already exist for today. Skipping initial data scraping.");
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Could not check for existing predictions or trigger initial scraping.");
    }
}
else
{
    using var scope = app.Services.CreateScope();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Skipping startup analysis/scrape queue because RUN_BACKGROUND_JOBS is disabled.");
}

// Configure the HTTP request pipeline
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// Baseline security headers for all responses.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    await next();
});

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseMiddleware<AdminUsageBasicAuthMiddleware>();
app.UseRouting();
app.UseRateLimiter();
if (runtimeMode.UserTrackingEnabled)
{
    app.UseMiddleware<UserTrackingMiddleware>();
}
else
{
    using var scope = app.Services.CreateScope();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Skipping user tracking middleware because ENABLE_USER_TRACKING is disabled.");
}

app.UseAuthorization();

// Start Hangfire Server and Dashboard
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    DashboardTitle = "Match Predictor Jobs",
    StatsPollingInterval = 5000,
    Authorization = app.Environment.IsDevelopment()
        ? new Hangfire.Dashboard.IDashboardAuthorizationFilter[] { new HangfireLocalRequestsOnlyFilter() }
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
