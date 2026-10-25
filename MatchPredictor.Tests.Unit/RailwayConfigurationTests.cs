using System.Collections;
using MatchPredictor.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class RailwayConfigurationTests
{
    [Fact]
    public void IsBookingEnabled_DefaultsToFalse_WhenUnconfigured()
    {
        var config = new ConfigurationBuilder().Build();
        Assert.False(config.IsBookingEnabled());
    }

    [Theory]
    [InlineData("ENABLE_BOOKING", "true")]
    [InlineData("ENABLE_BOOKING", "1")]
    [InlineData("ENABLE_BOOKING", "yes")]
    [InlineData("ENABLE_BOOKING", "on")]
    [InlineData("SPORTYBET_ENABLE_BOOKING", "true")]
    [InlineData("SPORTYBET_ENABLE_BOOKING", "1")]
    [InlineData("SPORTYBET__ENABLEBOOKING", "true")]
    [InlineData("SportyBet__EnableBooking", "true")]
    [InlineData("SportyBet:EnableBooking", "true")]
    public void IsBookingEnabled_ReturnsTrue_WhenTruthyConfigured(string key, string value)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        Assert.True(config.IsBookingEnabled());
    }

    [Theory]
    [InlineData("ENABLE_BOOKING", "false")]
    [InlineData("ENABLE_BOOKING", "0")]
    [InlineData("ENABLE_BOOKING", "no")]
    [InlineData("ENABLE_BOOKING", "off")]
    [InlineData("SPORTYBET_ENABLE_BOOKING", "false")]
    [InlineData("SportyBet:EnableBooking", "false")]
    public void IsBookingEnabled_ReturnsFalse_WhenFalsyConfigured(string key, string value)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        Assert.False(config.IsBookingEnabled());
    }

    [Fact]
    public void IsReviewSafePublicUi_DefaultsToTrue_WhenUnconfigured()
    {
        var config = new ConfigurationBuilder().Build();
        Assert.True(config.IsReviewSafePublicUi());
    }

    [Theory]
    [InlineData("ADSENSE_REVIEW_SAFE_PUBLIC_UI", "false")]
    [InlineData("ADSENSE_REVIEW_SAFE", "0")]
    [InlineData("REVIEW_SAFE_PUBLIC_UI", "false")]
    [InlineData("REVIEW_SAFE", "no")]
    [InlineData("AdSense:ReviewSafePublicUi", "false")]
    public void IsReviewSafePublicUi_ReturnsFalse_WhenDisabled(string key, string value)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        Assert.False(config.IsReviewSafePublicUi());
    }

    [Fact]
    public void AreAdUnitsEnabled_DefaultsToTrue_WhenUnconfigured()
    {
        var config = new ConfigurationBuilder().Build();
        Assert.True(config.AreAdUnitsEnabled());
    }

    [Theory]
    [InlineData("ADSENSE_ENABLE_AD_UNITS", "false")]
    [InlineData("ENABLE_AD_UNITS", "0")]
    [InlineData("ENABLE_ADSENSE", "no")]
    [InlineData("AdSense:EnableAdUnits", "false")]
    public void AreAdUnitsEnabled_ReturnsFalse_WhenDisabled(string key, string value)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        Assert.False(config.AreAdUnitsEnabled());
    }

    [Fact]
    public void GetMaxBookingSelections_ResolvesFromAliases()
    {
        var config1 = new ConfigurationBuilder().Build();
        Assert.Equal(50, config1.GetMaxBookingSelections());

        var config2 = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MAX_BOOKING_SELECTIONS"] = "35" })
            .Build();
        Assert.Equal(35, config2.GetMaxBookingSelections());

        var config3 = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SPORTYBET_MAX_BOOKING_SELECTIONS"] = "20" })
            .Build();
        Assert.Equal(20, config3.GetMaxBookingSelections());
    }

    [Fact]
    public void GetSelectionsPasskey_ResolvesFromAliases()
    {
        var config1 = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SELECTIONS_PASSKEY"] = "my-secret-key" })
            .Build();
        Assert.Equal("my-secret-key", config1.GetSelectionsPasskey());

        var config2 = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ADMIN_SELECTIONS_PASSKEY"] = "another-secret" })
            .Build();
        Assert.Equal("another-secret", config2.GetSelectionsPasskey());
    }

    [Fact]
    public void NormalizePostgresConnectionString_ConvertsPostgresUriToNpgsqlFormat()
    {
        var uri = "postgresql://postgres:secretpassword@roundhouse.proxy.rlwy.net:45832/railway";
        var converted = ConfigurationFlagExtensions.NormalizePostgresConnectionString(uri);

        Assert.NotNull(converted);
        Assert.Contains("Host=roundhouse.proxy.rlwy.net;", converted);
        Assert.Contains("Port=45832;", converted);
        Assert.Contains("Database=railway;", converted);
        Assert.Contains("Username=postgres;", converted);
        Assert.Contains("Password=secretpassword;", converted);
        Assert.Contains("SSL Mode=Require;", converted);
    }

    [Fact]
    public void NormalizePostgresConnectionString_PreservesStandardConnectionString()
    {
        var standard = "Host=ep-xxxx.neon.tech;Database=neondb;Username=user;Password=pass;SSL Mode=Require;";
        var result = ConfigurationFlagExtensions.NormalizePostgresConnectionString(standard);

        Assert.Equal(standard, result);
    }

    [Fact]
    public void RailwayConfigurationNormalizer_BuildNormalizedMap_PopulatesCanonicalKeys()
    {
        var fakeEnv = new Hashtable
        {
            ["ENABLE_BOOKING"] = "true",
            ["ADSENSE_REVIEW_SAFE"] = "false",
            ["MAX_BOOKING_SELECTIONS"] = "25",
            ["SELECTIONS_PASSKEY"] = "synergy-owner",
            ["DATABASE_URL"] = "postgresql://railwayuser:pass123@junction.proxy.rlwy.net:5432/proddb"
        };

        var map = RailwayConfigurationNormalizer.BuildNormalizedMap(existingConfig: null, envVars: fakeEnv);

        Assert.Equal("true", map["SportyBet:EnableBooking"]);
        Assert.Equal("false", map["AdSense:ReviewSafePublicUi"]);
        Assert.Equal("25", map["SportyBet:MaxBookingSelections"]);
        Assert.Equal("synergy-owner", map["AdminSettings:SelectionsPasskey"]);
        Assert.NotNull(map["ConnectionStrings:DefaultConnection"]);
        Assert.Contains("Host=junction.proxy.rlwy.net;", map["ConnectionStrings:DefaultConnection"]);
        Assert.Contains("Database=proddb;", map["ConnectionStrings:DefaultConnection"]);
    }
}
