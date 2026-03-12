using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class PredictionQueriesTests
{
    private static string? GetConnectionString() =>
        Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");

    private static ApplicationDbContext CreateContext()
    {
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings__DefaultConnection is not set. " +
                "Set it to a Neon/Postgres test database before running integration tests.");
        }

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new ApplicationDbContext(options);
    }

    [Fact]
    public async Task GetBTTSAsync_ReturnsInsertedPredictionForGivenDate()
    {
        if (string.IsNullOrWhiteSpace(GetConnectionString()))
        {
            return;
        }

        // Arrange
        await using var context = CreateContext();

        // Ensure database is up to date
        await context.Database.MigrateAsync();

        var testDate = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var dateString = testDate.ToString("dd-MM-yyyy");

        // Clean any previous test data for this key
        var existing = context.Predictions
            .Where(p => p.Date == dateString && p.League == "TestLeague" && p.HomeTeam == "Home" && p.AwayTeam == "Away");
        context.Predictions.RemoveRange(existing);
        await context.SaveChangesAsync();

        // Insert a test prediction row
        var prediction = new Prediction
        {
            Date = dateString,
            Time = "12:00",
            MatchDateTime = testDate.AddDays(1),
            League = "TestLeague",
            HomeTeam = "Home",
            AwayTeam = "Away",
            PredictionCategory = "BothTeamsScore",
            PredictedOutcome = "BTTS",
            CreatedAt = DateTime.UtcNow
        };

        context.Predictions.Add(prediction);
        await context.SaveChangesAsync();

        var queries = new PredictionQueries(context);

        // Act
        var results = await queries.GetBTTSAsync(testDate);

        // Assert
        Assert.Contains(results, p =>
            p is { League: "TestLeague", HomeTeam: "Home", AwayTeam: "Away", PredictionCategory: "BothTeamsScore" });
    }
}
