using Hangfire;
using Hangfire.Storage.SQLite;
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

builder.Services.AddDataProtection()
    .PersistKeysToDbContext<ApplicationDbContext>();

builder.Services.AddAntiforgery(options =>
{
    options.SuppressXFrameOptionsHeader = false;
    // Optionally reset token on validation failure
});

// Register application services
builder.Services.AddScoped<IMatchDataRepository, MatchDataRepository>();
builder.Services.AddScoped<IDataAnalyzerService, DataAnalyzerService>();
builder.Services.AddScoped<IWebScraperService, WebScraperService>();
builder.Services.AddScoped<IExtractFromExcel, ExtractFromExcel>();
builder.Services.AddScoped<IProbabilityCalculator, ProbabilityCalculator>();
builder.Services.AddScoped<IAnalyzerService, AnalyzerService>();
builder.Services.AddScoped<IRegressionPredictorService, RegressionPredictorService>();

// Configure data protection (keys stored in database)
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<ApplicationDbContext>();

// Configure logging
builder.Host.UseSerilog((context, services, configuration) =>
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
);

// Configure Hangfire with SQLite storage for jobs
var hangfireDatabasePath = Path.Combine(builder.Environment.ContentRootPath, "matchpredictor.db");
builder.Services.AddHangfire(config =>
    config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UseSQLiteStorage(hangfireDatabasePath));

builder.Services.AddHangfireServer();

// Configure Kestrel
var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.ListenAnyIP(int.Parse(port));
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();
app.UseAuthorization();

// Use Hangfire Dashboard
app.UseHangfireDashboard();

app.MapRazorPages();

// Seed database and start background jobs
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    db.Database.EnsureCreated();
    db.Database.Migrate();
}

// Hangfire job configuration
RecurringJob.AddOrUpdate<IAnalyzerService>(
    "RunScraperAndAnalyzer",
    service => service.RunScraperAndAnalyzerAsync(),
    Cron.Daily(2));

app.Run();


