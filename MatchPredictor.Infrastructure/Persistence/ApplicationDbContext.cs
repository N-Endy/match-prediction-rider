using MatchPredictor.Domain.Helpers;
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
    public DbSet<MetaModelProfile> MetaModelProfiles => Set<MetaModelProfile>();
    public DbSet<EnsembleWeightProfile> EnsembleWeightProfiles => Set<EnsembleWeightProfile>();
    public DbSet<IsotonicCalibrationProfile> IsotonicCalibrationProfiles => Set<IsotonicCalibrationProfile>();
    public DbSet<HistoricalBacktestSummary> HistoricalBacktestSummaries => Set<HistoricalBacktestSummary>();
    public DbSet<PromotionHistory> PromotionHistories => Set<PromotionHistory>();
    public DbSet<SourceQualityProfile> SourceQualityProfiles => Set<SourceQualityProfile>();
    public DbSet<PredictionOddsSnapshot> PredictionOddsSnapshots => Set<PredictionOddsSnapshot>();
    public DbSet<MarketOddsSnapshot> MarketOddsSnapshots => Set<MarketOddsSnapshot>();
    public DbSet<FixtureFeatureSnapshot> FixtureFeatureSnapshots => Set<FixtureFeatureSnapshot>();
    public DbSet<TeamMatchStats> TeamMatchStats => Set<TeamMatchStats>();
    public DbSet<MarketMlModelProfile> MarketMlModelProfiles => Set<MarketMlModelProfile>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamAlias> TeamAliases => Set<TeamAlias>();
    public DbSet<VisitorSession> VisitorSessions => Set<VisitorSession>();
    public DbSet<UserActivityEvent> UserActivityEvents => Set<UserActivityEvent>();
    public DbSet<BetslipSet> BetslipSets => Set<BetslipSet>();
    public DbSet<Betslip> Betslips => Set<Betslip>();
    public DbSet<BetslipSelection> BetslipSelections => Set<BetslipSelection>();
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
            // Closing-line snapshot job (every 5 min) and value-bet performance summary.
            entity.HasIndex(e => new { e.IsCurrentRevision, e.WasPublished, e.MatchDateTime });
            entity.HasIndex(e => new { e.SettledSourceName, e.SettledSourceEventId });
        });

        modelBuilder.Entity<MatchData>(entity =>
        {
            entity.HasIndex(e => new { e.MatchLocalDate, e.MatchLocalTime });
            entity.HasIndex(e => new { e.MatchLocalDate, e.League });
            entity.HasIndex(e => e.FixtureKey);
        });

        modelBuilder.Entity<MatchScore>(entity =>
        {
            entity.Property(e => e.HomeTeamKey).HasMaxLength(512);
            entity.Property(e => e.AwayTeamKey).HasMaxLength(512);
            entity.Property(e => e.LeagueKey).HasMaxLength(512);
            entity.HasIndex(e => new { e.MatchLocalDate, e.HomeTeamKey, e.AwayTeamKey, e.LeagueKey });
            entity.HasIndex(e => new { e.MatchTime, e.IsLive });
            // Finished scores only — empty keys excluded so incomplete scrapes do not collide.
            entity.HasIndex(e => new { e.MatchLocalDate, e.HomeTeamKey, e.AwayTeamKey, e.LeagueKey })
                .IsUnique()
                .HasFilter("\"IsLive\" = false AND \"HomeTeamKey\" <> '' AND \"AwayTeamKey\" <> '' AND \"LeagueKey\" <> ''")
                .HasDatabaseName("IX_MatchScores_FinishedFixture_Unique");
        });

        modelBuilder.Entity<AiScoreMatchScore>(entity =>
        {
            entity.Property(e => e.HomeTeamKey).HasMaxLength(512);
            entity.Property(e => e.AwayTeamKey).HasMaxLength(512);
            entity.Property(e => e.LeagueKey).HasMaxLength(512);
            entity.HasIndex(e => new { e.MatchLocalDate, e.HomeTeamKey, e.AwayTeamKey, e.LeagueKey });
            entity.HasIndex(e => new { e.MatchTime, e.IsLive });
            entity.HasIndex(e => e.SourceEventId);
        });

        modelBuilder.Entity<SofaScoreMatchScore>(entity =>
        {
            entity.Property(e => e.HomeTeamKey).HasMaxLength(512);
            entity.Property(e => e.AwayTeamKey).HasMaxLength(512);
            entity.Property(e => e.LeagueKey).HasMaxLength(512);
            entity.HasIndex(e => new { e.MatchLocalDate, e.HomeTeamKey, e.AwayTeamKey, e.LeagueKey });
            entity.HasIndex(e => new { e.MatchTime, e.IsLive });
            entity.HasIndex(e => e.EventId);
        });

        modelBuilder.Entity<ForecastObservation>(entity =>
        {
            entity.HasIndex(e => new { e.MatchLocalDate, e.IsCurrentRevision, e.Market, e.MatchLocalTime });
            entity.HasIndex(e => new { e.FixtureKey, e.IsCurrentRevision, e.Market });
            entity.HasIndex(e => new { e.PredictionRunId, e.Market });
            entity.HasIndex(e => new { e.PredictionRunId, e.FixtureKey, e.Market }).IsUnique();
            // Nightly calibration/threshold/meta-model rebuilds filter settled rows by window.
            entity.HasIndex(e => new { e.IsSettled, e.SettledAt });
            entity.HasIndex(e => new { e.SettledSourceName, e.SettledSourceEventId });
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

        modelBuilder.Entity<MarketOddsSnapshot>(entity =>
        {
            entity.HasIndex(e => new { e.MatchLocalDate, e.FixtureKey, e.SourceName });
            entity.HasIndex(e => e.CapturedAtUtc);
        });

        modelBuilder.Entity<FixtureFeatureSnapshot>(entity =>
        {
            entity.HasIndex(e => new { e.FixtureKey, e.CapturedAtUtc });
            entity.HasIndex(e => new { e.MatchLocalDate, e.FixtureKey });
        });

        modelBuilder.Entity<TeamMatchStats>(entity =>
        {
            entity.Property(e => e.LeagueKey).HasMaxLength(512);
            entity.Property(e => e.TeamName).HasMaxLength(TeamNameNormalizer.MaxIndexedValueLength);
            entity.Property(e => e.OpponentName).HasMaxLength(TeamNameNormalizer.MaxIndexedValueLength);
            entity.Property(e => e.SourceName).HasMaxLength(64);
            entity.Property(e => e.SourceMatchId).HasMaxLength(512);
            entity.HasIndex(e => new { e.SourceName, e.SourceMatchId, e.IsHome })
                .IsUnique()
                .HasFilter("\"SourceMatchId\" IS NOT NULL");
            entity.HasIndex(e => new { e.TeamName, e.KickoffUtc });
            entity.HasIndex(e => new { e.KickoffUtc, e.AvailableFromUtc });
        });

        modelBuilder.Entity<MarketMlModelProfile>(entity =>
        {
            entity.HasIndex(e => e.Market).IsUnique();
        });

        modelBuilder.Entity<Team>(entity =>
        {
            entity.Property(e => e.Name).HasMaxLength(TeamNameNormalizer.MaxIndexedValueLength);
            entity.Property(e => e.NormalizedName).HasMaxLength(TeamNameNormalizer.MaxIndexedValueLength);
            entity.Property(e => e.LeagueScope).HasMaxLength(TeamNameNormalizer.MaxIndexedValueLength);
            entity.HasIndex(e => new { e.NormalizedName, e.LeagueScope }).IsUnique();
        });

        modelBuilder.Entity<TeamAlias>(entity =>
        {
            entity.Property(e => e.Alias).HasMaxLength(TeamNameNormalizer.MaxIndexedValueLength);
            entity.Property(e => e.NormalizedAlias).HasMaxLength(TeamNameNormalizer.MaxIndexedValueLength);
            entity.Property(e => e.LeagueScope).HasMaxLength(TeamNameNormalizer.MaxIndexedValueLength);
            entity.Property(e => e.SourceName).HasMaxLength(TeamNameNormalizer.MaxIndexedValueLength);
            entity.HasIndex(e => new { e.NormalizedAlias, e.LeagueScope, e.SourceName }).IsUnique();
            entity.HasIndex(e => e.TeamId);
            entity.HasOne(e => e.Team)
                .WithMany(e => e.Aliases)
                .HasForeignKey(e => e.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ThresholdProfile>(entity =>
        {
            entity.HasIndex(e => e.Market).IsUnique();
        });

        modelBuilder.Entity<MetaModelProfile>(entity =>
        {
            entity.HasIndex(e => e.Market).IsUnique();
        });

        modelBuilder.Entity<EnsembleWeightProfile>(entity =>
        {
            entity.HasIndex(e => e.Market).IsUnique();
        });

        modelBuilder.Entity<BetaCalibrationProfile>(entity =>
        {
            entity.HasIndex(e => e.Market).IsUnique();
        });

        modelBuilder.Entity<IsotonicCalibrationProfile>(entity =>
        {
            entity.HasIndex(e => e.Market).IsUnique();
        });

        modelBuilder.Entity<HistoricalBacktestSummary>(entity =>
        {
            entity.HasIndex(e => e.RunAtUtc);
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
            entity.HasIndex(e => new { e.EventName, e.Timestamp });
            entity.HasIndex(e => e.Timestamp);
        });

        modelBuilder.Entity<BetslipSet>(entity =>
        {
            entity.Property(e => e.RunLabel).HasMaxLength(32);
            entity.Property(e => e.DayKind).HasMaxLength(16);
            entity.HasIndex(e => new { e.SlipLocalDate, e.IsCurrent });
            entity.HasIndex(e => e.GeneratedAtUtc);
            entity.HasMany(e => e.Slips)
                .WithOne(e => e.BetslipSet)
                .HasForeignKey(e => e.BetslipSetId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Betslip>(entity =>
        {
            entity.Property(e => e.Title).HasMaxLength(120);
            entity.Property(e => e.TierLabel).HasMaxLength(64);
            entity.Property(e => e.BookingCode).HasMaxLength(64);
            entity.Property(e => e.BookingUrl).HasMaxLength(512);
            entity.Property(e => e.BookingStatus).HasMaxLength(32);
            entity.Property(e => e.StatusMessage).HasMaxLength(512);
            entity.HasIndex(e => new { e.BetslipSetId, e.SlipNumber }).IsUnique();
            entity.HasMany(e => e.Selections)
                .WithOne(e => e.Betslip)
                .HasForeignKey(e => e.BetslipId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BetslipSelection>(entity =>
        {
            entity.Property(e => e.League).HasMaxLength(120);
            entity.Property(e => e.HomeTeam).HasMaxLength(120);
            entity.Property(e => e.AwayTeam).HasMaxLength(120);
            entity.Property(e => e.Market).HasMaxLength(32);
            entity.Property(e => e.PredictedOutcome).HasMaxLength(64);
            entity.Property(e => e.AiNote).HasMaxLength(512);
            entity.HasIndex(e => e.BetslipId);
            entity.HasIndex(e => e.PredictionId);
        });
    }
}
