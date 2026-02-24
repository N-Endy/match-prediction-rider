using Hangfire;
using Hangfire.Common;
using Hangfire.PostgreSql;
using MatchPredictor.Application.Services;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Infrastructure;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Repositories;
using MatchPredictor.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddRazorPages();
builder.Services.AddMemoryCache();
builder.Services.AddHealthChecks();

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

// Configure database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

// Register application services
builder.Services.AddScoped<IMatchDataRepository, MatchDataRepository>();
builder.Services.AddScoped<IPredictionQueries, PredictionQueries>();
builder.Services.AddScoped<IDataAnalyzerService, DataAnalyzerService>();
builder.Services.AddScoped<IWebScraperService, WebScraperService>();
builder.Services.AddScoped<IExtractFromExcel, ExtractFromExcel>();
builder.Services.AddScoped<IProbabilityCalculator, ProbabilityCalculator>();
builder.Services.AddScoped<IAnalyzerService, AnalyzerService>();
builder.Services.AddScoped<IRegressionPredictorService, RegressionPredictorService>();

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

builder.Services.AddHangfireServer();

// Configure Kestrel
var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.ListenAnyIP(int.Parse(port));
});

var app = builder.Build();

// Apply EF migrations (Postgres) — no local file/directory creation needed for Postgres
// using (var scope = app.Services.CreateScope())
// {
//     var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
//     db.Database.Migrate();
// }

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var logger = services.GetRequiredService<ILogger<Program>>();
    
    try
    {
        // Step 1: Migrate application database
        var context = services.GetRequiredService<ApplicationDbContext>();
        await context.Database.MigrateAsync();
        logger.LogInformation("Database initialized successfully.");
        
        // Step 2: Initialize Hangfire storage
        var storage = services.GetRequiredService<JobStorage>();
        
        // Force Hangfire to create its tables by accessing monitoring API
        var monitoringApi = storage.GetMonitoringApi();
        var stats = monitoringApi.GetStatistics();
        
        logger.LogInformation("✅ Hangfire initialized - Servers: {StatsServers}, Jobs: {StatsRecurring}", stats.Servers, stats.Recurring);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "❌ Database initialization error: {ExMessage}", ex.Message);
        // Don't throw - allow app to continue but log error
    }
}

// Register recurring Hangfire jobs properly
using (var scope = app.Services.CreateScope())
{
    var recurringJobs = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    recurringJobs.AddOrUpdate<IAnalyzerService>(
        "daily-prediction-job",
        service => service.RunScraperAndAnalyzerAsync(),
        "*/15 * * * *", // Every 15 minutes
        new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Local
        }
    );

    recurringJobs.AddOrUpdate<IAnalyzerService>(
        "cleanup-old-predictions",
        service => service.CleanupOldPredictionsAndMatchDataAsync(),
        "0 1 * * *", // Daily at 1:00 AM
        new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Local
        }
    );
    logger.LogInformation("Recurring jobs registered successfully.");
}

// Auto-trigger initial data scraping if no predictions exist for today
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    
    try
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var hasTodaysPredictions = await db.Predictions.AnyAsync(p => p.Date == today);
        
        if (!hasTodaysPredictions)
        {
            logger.LogInformation("No predictions found for today. Triggering initial data scraping...");
            var backgroundJobs = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>();
            backgroundJobs.Enqueue<IAnalyzerService>(service => service.RunScraperAndAnalyzerAsync());
            logger.LogInformation("✅ Initial scraping job queued successfully.");
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

// Configure the HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.UseAuthorization();

// Start Hangfire Server and Dashboard
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    DashboardTitle = "Match Predictor Jobs",
    StatsPollingInterval = 5000
});

app.MapRazorPages();
app.MapHealthChecks("/health");

app.Run();


