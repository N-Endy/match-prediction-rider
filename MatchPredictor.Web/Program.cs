using Hangfire;
using Hangfire.Common;
using Hangfire.MemoryStorage;
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
    options.UseSqlite(connectionString)
           .ConfigureWarnings(warnings => 
               warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

// Register application services
builder.Services.AddScoped<IMatchDataRepository, MatchDataRepository>();
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
          .UseMemoryStorage();  // Using memory storage for development

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

// Ensure database directory and file exist
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var dbPath = db.Database.GetConnectionString()?.Replace("Data Source=", "");
    if (!string.IsNullOrEmpty(dbPath))
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }
    db.Database.Migrate();
}

// Register recurring Hangfire jobs properly
using (var scope = app.Services.CreateScope())
{
    var recurringJobs = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();

    recurringJobs.AddOrUpdate<AnalyzerService>(
        "daily-prediction-job",
        service => service.RunScraperAndAnalyzerAsync(),
        Cron.Hourly(5),   // Every hour at minute 5
        new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc
        }
    );

    recurringJobs.AddOrUpdate<AnalyzerService>(
        "cleanup-old-predictions",
        service => service.CleanupOldPredictionsAndMatchDataAsync(),
        "0 1 * * *", // Daily at 1:00 AM
        new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc
        }
    );
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
            backgroundJobs.Enqueue<AnalyzerService>(service => service.RunScraperAndAnalyzerAsync());
            logger.LogInformation("Initial scraping job queued successfully.");
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
    DashboardTitle = "Match Predictor Jobs"
});

app.MapRazorPages();
app.MapHealthChecks("/health");

app.Run();