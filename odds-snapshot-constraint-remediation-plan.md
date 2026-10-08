# Odds Snapshot Constraint Remediation Plan

## Goal
Resolve PostgreSQL unique constraint violation `23505` on `PredictionOddsSnapshots` during periodic `CaptureInterimOddsSnapshotsAsync` execution by converting the unique constraint to a partial unique index for lifecycle-singular snapshots (`Publish` and `Close`) while allowing time-series `Interim` snapshots.

---

## Architecture & Root Cause Context
- **Table**: `PredictionOddsSnapshots`
- **Constraint**: `IX_PredictionOddsSnapshots_PredictionId_SourceName_SnapshotKind` UNIQUE on `(PredictionId, SourceName, SnapshotKind)`
- **Problem**: Background job `interim-odds-snapshot-job` runs every 15 minutes. It snapshots upcoming matches (>1h, <24h). Once an `Interim` snapshot is older than 2 hours, `MarketTimingService` attempts to record a new interim snapshot to build the price velocity trajectory ($v = \Delta \text{odds}/\Delta t$). Because the database index was globally unique, Postgres rejects any second `Interim` row with error `23505`.
- **Solution**: 
  1. Convert the unique constraint in `ApplicationDbContext` to a PostgreSQL partial unique index scoped strictly to `Publish` (1) and `Close` (2), where singletons are required for EV/CLV evaluation.
  2. Allow multiple time-series records for `Interim` (4) and `Steam` (5).
  3. Harden `CaptureInterimOddsSnapshotsAsync` against batch duplicates.

---

## Tasks

- [x] **Task 1: Update EF Core Entity Configuration in `ApplicationDbContext.cs`**
  - Path: `MatchPredictor.Infrastructure/Persistence/ApplicationDbContext.cs`
  - Action: Update `modelBuilder.Entity<PredictionOddsSnapshot>`:
    ```csharp
    entity.HasIndex(e => new { e.PredictionId, e.SourceName, e.SnapshotKind })
        .IsUnique()
        .HasFilter("\"SnapshotKind\" IN (1, 2)");
    ```
  - Verify: Build succeeds without syntax or schema errors (`dotnet build MatchPredictor.Infrastructure`).

- [x] **Task 2: Generate EF Core Migration**
  - Action: Run `dotnet ef migrations add UpdatePredictionOddsSnapshotUniqueFilter --project MatchPredictor.Infrastructure --startup-project MatchPredictor.Web`
  - Review migration file:
    - `migrationBuilder.DropIndex(name: "IX_PredictionOddsSnapshots_PredictionId_SourceName_SnapshotKind", table: "PredictionOddsSnapshots");`
    - `migrationBuilder.CreateIndex(name: "IX_PredictionOddsSnapshots_PredictionId_SourceName_SnapshotKind", table: "PredictionOddsSnapshots", columns: new[] { "PredictionId", "SourceName", "SnapshotKind" }, unique: true, filter: "\"SnapshotKind\" IN (1, 2)");`
  - Verify: Migration files and snapshot compile cleanly.

- [x] **Task 3: Harden `MarketTimingService.CaptureInterimOddsSnapshotsAsync`**
  - Path: `MatchPredictor.Application/Services/MarketTimingService.cs`
  - Action:
    - Ensure `predictionsToSnapshot` is deduplicated by `PredictionId` before processing: `.DistinctBy(p => p.Id)`.
    - Ensure in-memory list `newSnapshots` cannot contain multiple snapshots for the same `PredictionId` within the single batch.
    - Wrap the save operation with structured diagnostic logging so any individual booking or odds provider failure does not leave Hangfire in an unhandled crash state.
  - Verify: Code compiles and passes static analysis.

- [x] **Task 4: Add Automated Unit / Integration Tests for Interim Snapshots**
  - Path: `MatchPredictor.Tests.Integration/MarketTimingServiceTests.cs`
  - Action: Add test cases:
    - `CaptureInterimOddsSnapshotsAsync_SavesInitialInterimSnapshot`: Verifies first snapshot creation.
    - `CaptureInterimOddsSnapshotsAsync_WithinTwoHours_SkipsSnapshot`: Verifies no duplicate snapshot created within 2h window.
    - `CaptureInterimOddsSnapshotsAsync_AfterTwoHours_RecordsSubsequentSnapshot`: Verifies subsequent snapshot is recorded after 2h for velocity tracking.
  - Verify: `dotnet test --filter MarketTimingServiceTests` passes 100%.

- [x] **Task 5: End-to-End Regression Verification**
  - Action: Run all test suites touching odds snapshots:
    - `MarketTimingServiceTests`
    - `ForecastEvaluationService` / `AnalyzerServiceBackfillTests`
    - `BettingPerformanceStatsTests`
  - Verify: All tests pass (`dotnet test`).

---

## Done When
- [x] Partial unique index is defined in `ApplicationDbContext` and backed by a new EF Core migration.
- [x] `MarketTimingService.CaptureInterimOddsSnapshotsAsync` successfully captures multi-point snapshots spaced >2h apart without throwing Postgres unique constraint `23505`.
- [x] Singletons for `Publish` and `Close` remain strictly enforced for EV and CLV settlement accuracy.
- [x] Full solution builds with 0 warnings/errors and all tests pass.
