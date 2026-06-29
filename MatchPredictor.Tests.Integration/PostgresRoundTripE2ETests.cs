using MatchPredictor.Domain.Models;
using MatchPredictor.Domain.Sourcing;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MatchPredictor.Tests.Integration;

/// <summary>
/// End-to-end tests that run against a real PostgreSQL instance (the same provider used in
/// production), exercising EF Core migrations and round-trip persistence. They are tagged
/// <c>PostgresE2E</c> and excluded from the fast PR pipeline; the nightly CI job provides a
/// Postgres service container and a connection string via the
/// <c>MATCHPREDICTOR_TEST_POSTGRES</c> (or <c>ConnectionStrings__DefaultConnection</c>)
/// environment variable. When no connection string is configured the tests no-op so they are
/// safe to run locally without a database.
/// </summary>
[Trait("Category", "PostgresE2E")]
public class PostgresRoundTripE2ETests
{
    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("MATCHPREDICTOR_TEST_POSTGRES")
        ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");

    private static DbContextOptions<ApplicationDbContext> BuildOptions(string connectionString) =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .Options;

    [Fact]
    public async Task Migrations_Apply_AndMatchScoreRoundTrips()
    {
        var connectionString = ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return; // No Postgres configured — skip silently (see class remarks).
        }

        await using var context = new ApplicationDbContext(BuildOptions(connectionString));
        await context.Database.MigrateAsync();

        var unique = Guid.NewGuid().ToString("N")[..8];
        var score = new MatchScore
        {
            League = $"E2E-{unique}",
            HomeTeam = "Alpha",
            AwayTeam = "Beta",
            Score = "2:1",
            MatchTime = new DateTime(2026, 1, 1, 15, 0, 0, DateTimeKind.Utc),
            BTTSLabel = true,
            IsLive = false
        };

        context.MatchScores.Add(score);
        await context.SaveChangesAsync();

        await using var verifyContext = new ApplicationDbContext(BuildOptions(connectionString));
        var persisted = await verifyContext.MatchScores.SingleAsync(s => s.League == $"E2E-{unique}");

        Assert.Equal("Alpha", persisted.HomeTeam);
        Assert.Equal("2:1", persisted.Score);
        Assert.True(persisted.BTTSLabel);
        Assert.Equal(DateTimeKind.Utc, persisted.MatchTime.Kind);

        verifyContext.MatchScores.Remove(persisted);
        await verifyContext.SaveChangesAsync();
    }

    [Fact]
    public async Task HistoricalDatasetService_ExportImport_RoundTripsThroughPostgres()
    {
        var connectionString = ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return; // No Postgres configured — skip silently.
        }

        await using var context = new ApplicationDbContext(BuildOptions(connectionString));
        await context.Database.MigrateAsync();

        var unique = Guid.NewGuid().ToString("N")[..8];
        var league = $"E2E-DS-{unique}";

        var seeded = new MatchScore
        {
            League = league,
            HomeTeam = "Gamma",
            AwayTeam = "Delta",
            Score = "0:0",
            MatchTime = new DateTime(2026, 2, 2, 18, 30, 0, DateTimeKind.Utc),
            BTTSLabel = false,
            IsLive = false
        };
        context.MatchScores.Add(seeded);
        await context.SaveChangesAsync();

        var service = new HistoricalDatasetService(context);
        var json = await service.ExportAsync(lookbackDays: 3650);
        Assert.Contains(league, json);

        var restored = HistoricalDatasetSerializer.Deserialize(json);
        Assert.Contains(restored, s => s.League == league && s.HomeTeam == "Gamma");

        // Clean up the row this test created.
        var toRemove = await context.MatchScores.Where(s => s.League == league).ToListAsync();
        context.MatchScores.RemoveRange(toRemove);
        await context.SaveChangesAsync();
    }
}
