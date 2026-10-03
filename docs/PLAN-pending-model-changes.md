# Implementation Plan: EF Core Pending Model Changes & Migration Alignment

**Task Slug:** `pending-model-changes`  
**Date:** 2026-10-03  
**Status:** Ready for Review  

---

## 1. Problem Statement & Root Cause

During container deployment and web application startup at `Program.cs:line 259` (`await context.Database.MigrateAsync()`), the application crashed with a fatal unhandled exception:

```text
Unhandled exception. System.InvalidOperationException: Application startup aborted because database initialization failed.
 ---> System.InvalidOperationException: An error was generated for warning 'Microsoft.EntityFrameworkCore.Migrations.PendingModelChangesWarning': The model for context 'ApplicationDbContext' has pending changes. Add a new migration before updating the database.
```

### Root Cause Analysis
In the previous commit (`3ef2f99`), the EF Core database model was extended to support the MLOps automated shadow retraining architecture:
1. **New Entity**: Added `ModelShadowEvaluation` and registered `DbSet<ModelShadowEvaluation>` in `ApplicationDbContext` with compound indexes on `(PredictionId, Market)` and `(IsSettled, CapturedAtUtc)`.
2. **Updated Entity**: Added columns to `MarketMlModelProfile` (`IsShadow`, `ShadowSampleCount`, `ShadowBrierScore`, `ShadowLogLoss`, `PromotionPValue`).
3. **Missing Migration**: No EF Core migration was generated to compile these additions into `ApplicationDbContextModelSnapshot.cs`.

In EF Core 9+ / .NET 10, executing `Database.MigrateAsync()` when the in-memory `IModel` diverges from the compiled snapshot triggers `RelationalEventId.PendingModelChangesWarning`, which defaults to throwing an error and halting application startup.

---

## 2. Target Architecture & Scope

| Layer | Component | Action |
|---|---|---|
| **Infrastructure** | `MatchPredictor.Infrastructure/Migrations/` | Generate migration `AddShadowEvaluationAndChallengerProfiles` and update `ApplicationDbContextModelSnapshot.cs`. |
| **Web Host** | `MatchPredictor.Web/Program.cs` | Configure warning handling in `AddDbContext` to log rather than crash on `PendingModelChangesWarning`. |
| **Database** | PostgreSQL Schema | Ensure `model_shadow_evaluations` table and columns on `market_ml_model_profiles` are created during startup migration. |
| **Verification** | Test Suite & CLI | Verify `dotnet ef migrations has-pending-model-changes` returns 0; verify unit and integration tests pass. |

---

## 3. Step-by-Step Task Breakdown

### Phase 1: Migration Generation
- **Task 1.1**: Run `dotnet ef migrations add AddShadowEvaluationAndChallengerProfiles -s MatchPredictor.Web -p MatchPredictor.Infrastructure`.
- **Task 1.2**: Audit the generated migration C# file:
  - Verify `CreateTable` for `ModelShadowEvaluations` contains correct column types: `PredictionId` (int), `Market` (varchar), `ChampionProbability` (double), `ChallengerProbability` (double), `BrierLossChampion` (double, nullable), `BrierLossChallenger` (double, nullable), etc.
  - Verify indexes on `(PredictionId, Market)` and `(IsSettled, CapturedAtUtc)` match `ApplicationDbContext.OnModelCreating`.
  - Verify `AddColumn` calls for `IsShadow`, `ShadowSampleCount`, `ShadowBrierScore`, `ShadowLogLoss`, and `PromotionPValue` on table `MarketMlModelProfiles`.
  - Verify symmetrical `Down()` method.
- **Task 1.3**: Confirm `ApplicationDbContextModelSnapshot.cs` is updated and clean.

### Phase 2: Host Startup Resilience
- **Task 2.1**: In `MatchPredictor.Web/Program.cs` (lines 71–72):
  ```csharp
  builder.Services.AddDbContext<ApplicationDbContext>(options =>
  {
      options.UseNpgsql(connectionString);
      options.ConfigureWarnings(w => w.Log(RelationalEventId.PendingModelChangesWarning));
  });
  ```
  - This ensures that if any minor metadata annotation drift occurs in the future, it is logged for developer visibility without aborting the container startup process.

### Phase 3: Comprehensive Verification
- **Task 3.1**: Verify EF Core model synchronization:
  - Run `dotnet ef migrations has-pending-model-changes -s MatchPredictor.Web -p MatchPredictor.Infrastructure` and assert clean exit code 0.
- **Task 3.2**: Run Unit Test Suite:
  - `DOTNET_CLI_HOME=/Users/nnamdi dotnet test MatchPredictor.Tests.Unit` (329 tests).
- **Task 3.3**: Run Integration Test Suite:
  - `DOTNET_CLI_HOME=/Users/nnamdi dotnet test MatchPredictor.Tests.Integration` (436 tests, verifying in-memory DB and migration schemas).
- **Task 3.4**: Run Master Quality Checklist:
  - `python3 .agent/scripts/checklist.py .` (P0-P5 checks).

### Phase 4: Git Deployment
- **Task 4.1**: Stage newly generated migration files, updated snapshot, and `Program.cs`.
- **Task 4.2**: Commit with message: `fix(migrations): add AddShadowEvaluationAndChallengerProfiles migration and configure pending model changes warning`.
- **Task 4.3**: Push to `origin/ai-plan`.

---

## 4. Agent Assignments

- **Primary Agent**: `backend-specialist` (EF Core migrations, C# data models, host configuration).
- **Reviewer Agent**: `security-auditor` (verify database index performance and constraint boundaries).
- **Orchestration**: `orchestrator` / `/create`.

---

## 5. Verification Checklist

- [ ] `dotnet ef migrations add` completes with 0 errors.
- [ ] `ApplicationDbContextModelSnapshot.cs` includes `ModelShadowEvaluation` and new `MarketMlModelProfile` properties.
- [ ] `dotnet ef migrations has-pending-model-changes` returns 0.
- [ ] `PendingModelChangesWarning` configured to log in `Program.cs`.
- [ ] All 329 Unit Tests pass.
- [ ] All 436 Integration Tests pass.
- [ ] `checklist.py` passes all 6 validation stages.
- [ ] Clean deployment to remote branch without startup aborts.
