using MatchPredictor.Domain.Models;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace MatchPredictor.Infrastructure.Persistence;

public class ApplicationDbContext : DbContext, IDataProtectionKeyContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options) { }

    public DbSet<MatchData> MatchDatas => Set<MatchData>();
    public DbSet<Prediction> Predictions => Set<Prediction>();
    public DbSet<ScrapingLog> ScrapingLogs => Set<ScrapingLog>();
    public DbSet<MatchScore> MatchScores => Set<MatchScore>();
    // Required by IDataProtectionKeyContext
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure all DateTime properties to use UTC
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
                {
                    property.SetValueConverter(
                        new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
                            v => v.ToUniversalTime(),
                            v => DateTime.SpecifyKind(v, DateTimeKind.Utc)));
                }
            }
        }

        // Optional: Configure DataProtectionKeys table name explicitly
        modelBuilder.Entity<DataProtectionKey>(entity =>
        {
            entity.ToTable("DataProtectionKeys");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FriendlyName).HasColumnType("TEXT");
            entity.Property(e => e.Xml).HasColumnType("TEXT");
        });
    }
}
