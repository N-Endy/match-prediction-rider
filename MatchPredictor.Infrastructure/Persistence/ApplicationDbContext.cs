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
    public DbSet<ForecastObservation> ForecastObservations => Set<ForecastObservation>();
    public DbSet<PredictionRun> PredictionRuns => Set<PredictionRun>();
    public DbSet<RegressionPrediction> RegressionPredictions => Set<RegressionPrediction>();
    public DbSet<ScrapingLog> ScrapingLogs => Set<ScrapingLog>();
    public DbSet<MatchScore> MatchScores => Set<MatchScore>();
    public DbSet<AiScoreMatchScore> AiScoreMatchScores => Set<AiScoreMatchScore>();
    public DbSet<SofaScoreMatchScore> SofaScoreMatchScores => Set<SofaScoreMatchScore>();
    public DbSet<ModelAccuracy> ModelAccuracies => Set<ModelAccuracy>();
    public DbSet<MarketCalibrationProfile> MarketCalibrationProfiles => Set<MarketCalibrationProfile>();
    public DbSet<BetaCalibrationProfile> BetaCalibrationProfiles => Set<BetaCalibrationProfile>();
    public DbSet<ThresholdProfile> ThresholdProfiles => Set<ThresholdProfile>();
    public DbSet<PromotionHistory> PromotionHistories => Set<PromotionHistory>();
    public DbSet<SourceQualityProfile> SourceQualityProfiles => Set<SourceQualityProfile>();
    public DbSet<PredictionOddsSnapshot> PredictionOddsSnapshots => Set<PredictionOddsSnapshot>();
    public DbSet<VisitorSession> VisitorSessions => Set<VisitorSession>();
    public DbSet<UserActivityEvent> UserActivityEvents => Set<UserActivityEvent>();
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

        modelBuilder.Entity<MarketCalibrationProfile>(entity =>
        {
            entity.HasIndex(e => new { e.Market, e.BucketStart }).IsUnique();
        });

        modelBuilder.Entity<Prediction>(entity =>
        {
            entity.HasIndex(e => new { e.MatchLocalDate, e.IsCurrentRevision, e.PredictionCategory, e.MatchLocalTime });
            entity.HasIndex(e => new { e.MatchLocalDate, e.IsCurrentRevision, e.League });
            entity.HasIndex(e => new { e.FixtureKey, e.IsCurrentRevision });
            entity.HasIndex(e => new { e.PredictionRunId, e.PredictionCategory });
            entity.HasIndex(e => new { e.PredictionRunId, e.FixtureKey, e.PredictionCategory }).IsUnique();
        });

        modelBuilder.Entity<MatchData>(entity =>
        {
            entity.HasIndex(e => new { e.MatchLocalDate, e.MatchLocalTime });
            entity.HasIndex(e => new { e.MatchLocalDate, e.League });
            entity.HasIndex(e => e.FixtureKey);
        });

        modelBuilder.Entity<MatchScore>(entity =>
        {
            entity.HasIndex(e => new { e.MatchTime, e.HomeTeam, e.AwayTeam });
            entity.HasIndex(e => new { e.MatchTime, e.IsLive });
        });

        modelBuilder.Entity<AiScoreMatchScore>(entity =>
        {
            entity.HasIndex(e => new { e.MatchTime, e.HomeTeam, e.AwayTeam });
            entity.HasIndex(e => new { e.MatchTime, e.IsLive });
        });

        modelBuilder.Entity<SofaScoreMatchScore>(entity =>
        {
            entity.HasIndex(e => new { e.MatchTime, e.HomeTeam, e.AwayTeam });
            entity.HasIndex(e => new { e.MatchTime, e.IsLive });
            entity.HasIndex(e => e.EventUrl);
        });

        modelBuilder.Entity<ForecastObservation>(entity =>
        {
            entity.HasIndex(e => new { e.MatchLocalDate, e.IsCurrentRevision, e.Market, e.MatchLocalTime });
            entity.HasIndex(e => new { e.FixtureKey, e.IsCurrentRevision, e.Market });
            entity.HasIndex(e => new { e.PredictionRunId, e.Market });
            entity.HasIndex(e => new { e.PredictionRunId, e.FixtureKey, e.Market }).IsUnique();
        });

        modelBuilder.Entity<PredictionRun>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.TargetLocalDate, e.StartedAtUtc });
            entity.HasIndex(e => new { e.RunKind, e.StartedAtUtc });
        });

        modelBuilder.Entity<PredictionOddsSnapshot>(entity =>
        {
            entity.HasIndex(e => new { e.PredictionId, e.SourceName, e.SnapshotKind }).IsUnique();
            entity.HasIndex(e => new { e.SnapshotKind, e.CapturedAtUtc });
            entity.HasIndex(e => new { e.PredictionRunId, e.SnapshotKind });
        });

        modelBuilder.Entity<ThresholdProfile>(entity =>
        {
            entity.HasIndex(e => e.Market).IsUnique();
        });

        modelBuilder.Entity<BetaCalibrationProfile>(entity =>
        {
            entity.HasIndex(e => e.Market).IsUnique();
        });

        modelBuilder.Entity<PromotionHistory>(entity =>
        {
            entity.HasIndex(e => new { e.EffectiveAt, e.Market });
        });

        modelBuilder.Entity<SourceQualityProfile>(entity =>
        {
            entity.HasIndex(e => new { e.SourceName, e.LeagueKey, e.TimeBucketKey }).IsUnique();
            entity.HasIndex(e => new { e.SourceName, e.ReliabilityScore });
        });

        modelBuilder.Entity<VisitorSession>(entity =>
        {
            entity.HasIndex(e => e.VisitorId);
            entity.HasIndex(e => e.SessionId).IsUnique();
            entity.HasIndex(e => e.LastSeenAt);
        });

        modelBuilder.Entity<UserActivityEvent>(entity =>
        {
            entity.HasIndex(e => new { e.CreatedAt, e.EventType });
            entity.HasIndex(e => new { e.SessionId, e.CreatedAt });
        });

        modelBuilder.Entity<ScrapingLog>(entity =>
        {
            entity.Property(e => e.EventName)
                .HasMaxLength(64)
                .HasDefaultValue("general");
            entity.Property(e => e.SourceName)
                .HasMaxLength(64);
            entity.Property(e => e.Stage)
                .HasMaxLength(64);
            entity.Property(e => e.RunKind)
                .HasMaxLength(64);
            entity.Property(e => e.RunLabel)
                .HasMaxLength(128);
            entity.HasIndex(e => new { e.EventName, e.Timestamp });
            entity.HasIndex(e => new { e.SourceName, e.Stage, e.Timestamp });
            entity.HasIndex(e => new { e.RunKind, e.RunLabel, e.Timestamp });
            entity.HasIndex(e => e.PredictionRunId);
            entity.HasIndex(e => e.Timestamp);
        });
    }
}
