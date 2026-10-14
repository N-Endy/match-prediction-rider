# Implementation Plan: Fix ML Promotion, Shadow Evaluation & Calibration Wiring

**File:** `docs/PLAN-fix-ml-promotion-wiring.md`  
**Date:** October 14, 2026  
**Status:** DRAFT (Awaiting User Review)  
**Primary Agent:** `project-planner` / `backend-specialist` / `debugger`  
**Associated Issues:**
1. Under 2.5 ML model bypassed in `DataAnalyzerService.BlendWithPerMarketWeights`.
2. Database unique constraint `IX_MarketMlModelProfiles_Market` crashes `TrainChallengerProfilesAsync`.
3. Shadow evaluation pipeline completely unwired (`TryPredictChallenger` and `LogShadowPredictionAsync` never invoked).
4. `ResolveEnsembleBaselineProbability` uses static default weights instead of market-specific tuned weights.
5. Diagnostics UI lacks distinction between champion models, shadow challengers, and unpromoted candidates.

---

## Architecture Context & Problem Analysis

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                       CURRENT STATE & DEFECTS                               │
└─────────────────────────────────────────────────────────────────────────────┘
  1. DataAnalyzerService.BlendWithPerMarketWeights
     ├── Calls BlendFor() on Over25Goals (mlValue is null)
     └── Hardcodes Under25 = 1.0 - Over25  ──> [BUG 1: Promoted Under 2.5 ML bypassed!]

  2. Hangfire: challenger-training-job (Weekly Mon 03:00)
     ├── Trains challenger models (IsPromoted = false, IsShadow = true)
     └── Saves to MarketMlModelProfiles  ──> [BUG 2: Postgres 23505 Unique Constraint Crash!]
         (Index IX_MarketMlModelProfiles_Market is unique on Market alone)

  3. Live Prediction Pipeline
     ├── TryPredictChallenger() is NEVER called
     └── LogShadowPredictionAsync() is NEVER called ──> [BUG 3: ModelShadowEvaluations is 0 rows!]

  4. MarketPredictionModelService.ResolveEnsembleBaselineProbability
     └── Uses EnsembleWeights.ProductionDefault instead of tuned weights ──> [BUG 4: Baseline skew]
```

---

## Phase Breakdown

### Phase 1: Database Schema & Migration Fix
**Goal:** Allow champion (`IsPromoted=true, IsShadow=false`) and challenger (`IsPromoted=false, IsShadow=true`) to coexist in `MarketMlModelProfiles` per market.

- [ ] **Task 1.1: Update Entity Configuration in `ApplicationDbContext.cs`**
  - **File:** `MatchPredictor.Infrastructure/Persistence/ApplicationDbContext.cs`
  - **Action:** Replace single-column unique index `entity.HasIndex(e => e.Market).IsUnique()` with:
    - Unique filtered index for active champion: `entity.HasIndex(e => e.Market).IsUnique().HasFilter("\"IsPromoted\" = true");`
    - Unique filtered index for active shadow challenger: `entity.HasIndex(e => e.Market).IsUnique().HasFilter("\"IsShadow\" = true");`
    - Standard non-unique index on `Market` for query lookups.
  - **Verification:** Model snapshot reflects filtered unique indices without schema regression.

- [ ] **Task 1.2: Generate & Verify EF Core Migration**
  - **File:** `MatchPredictor.Infrastructure/Migrations/*_AllowChampionAndChallengerProfiles.cs`
  - **Command:** `dotnet ef migrations add AllowChampionAndChallengerProfiles --project MatchPredictor.Infrastructure --startup-project MatchPredictor.Web`
  - **Verification:** Migration script drops `IX_MarketMlModelProfiles_Market` and creates the two filtered unique indices.

---

### Phase 2: Totals ML Blending Fix
**Goal:** Ensure promoted ML models for `Under25Goals` (and future `Over25Goals`) are actively blended into probabilities at inference time.

- [ ] **Task 2.1: Fix `BlendWithPerMarketWeights` in `DataAnalyzerService.cs`**
  - **File:** `MatchPredictor.Infrastructure/Services/DataAnalyzerService.cs`
  - **Location:** Lines 464–485 (`BlendWithPerMarketWeights`)
  - **Logic:**
    1. Call `BlendFor(PredictionMarket.Over25Goals, bookmaker?.Over25, calculator.Over25, statistical?.Over25)`
    2. Call `BlendFor(PredictionMarket.Under25Goals, bookmaker?.Under25, calculator.Under25, statistical?.Under25)`
    3. If neither or only one has a promoted ML signal:
       - If Under 2.5 has ML and Over 2.5 does not: blend Under 2.5 with its ML signal, and define Over 2.5 as $1.0 - P(\text{Under25})$ (or normalize if both have quotes).
       - If both or neither have ML signals: normalize $P(\text{Over25}) + P(\text{Under25}) = 1.0$ while preserving the blended ratio.
  - **Verification:** Unit test asserting that when `_marketPredictionModelService.TryPredict(match, Under25Goals)` returns a probability, the candidate probability reflects the ML weight.

---

### Phase 3: Wire Shadow Evaluation Pipeline
**Goal:** Enable shadow prediction execution, logging, and statistical promotion gate settlement.

- [ ] **Task 3.1: Wire `TryPredictChallenger` in `DataAnalyzerService.cs`**
  - **File:** `MatchPredictor.Infrastructure/Services/DataAnalyzerService.cs`
  - **Action:** In `BuildForecastCandidatesForMatch` / `BuildFeatureContributionSummary`:
    - For each evaluated market, check if `_marketPredictionModelService.TryPredictChallenger(...)` returns a value.
    - Attach challenger probability to the prediction candidate or metadata.

- [ ] **Task 3.2: Wire `LogShadowPredictionAsync` on Prediction Persistence**
  - **File:** `MatchPredictor.Application/Services/AnalyzerService.PredictionGeneration.cs` (or prediction persistence pipeline)
  - **Action:** When new predictions are inserted into the database:
    - If a challenger prediction was computed for the market, call:
      ```csharp
      await _marketPredictionModelService.LogShadowPredictionAsync(
          prediction.Id,
          prediction.FixtureKey,
          market,
          prediction.PredictedOutcome,
          championProbability: candidate.CalibratedProbability,
          challengerProbability: challengerCalibratedProbability,
          cancellationToken);
      ```
  - **Verification:** Running a test prediction generation batch creates rows in `ModelShadowEvaluations`.

- [ ] **Task 3.3: Verify `shadow-evaluation-settle-job` Execution**
  - **File:** `MatchPredictor.Infrastructure/Services/MarketPredictionModelService.cs#L353-L450`
  - **Action:** Ensure settled predictions update `ChampionBrierLoss`, `ChallengerBrierLoss`, and trigger `ModelPromotionGate.Evaluate` once $\ge 100$ samples accumulate.

---

### Phase 4: Market-Aware Baseline Brier Score Calculation
**Goal:** Prevent static default weights from skewing candidate model evaluation.

- [ ] **Task 4.1: Inject or Resolve Market-Specific Ensemble Weights in `MarketPredictionModelService.cs`**
  - **File:** `MatchPredictor.Infrastructure/Services/MarketPredictionModelService.cs`
  - **Action:** In `ResolveEnsembleBaselineProbability(MarketModelInput row, PredictionMarket market)`:
    - Query `EnsembleWeightProfiles` (or cached dictionary) for `market`.
    - Fall back to `EnsembleWeights.ProductionDefault` only if no tuned profile exists for that market.
  - **Verification:** Baseline Brier score during training reflects real production ensemble performance.

---

### Phase 5: Diagnostics Page & UI Enhancements
**Goal:** Provide clear visibility into champions, challengers, unpromoted candidates, and calibration status.

- [ ] **Task 5.1: Update `AnalyticsQueries.cs` Model Profiling**
  - **File:** `MatchPredictor.Web/Services/AnalyticsQueries.cs`
  - **Action:** Fetch both champion and shadow profiles, clearly delineating role in `MarketMlProfiles`.

- [ ] **Task 5.2: Update `Analytics.cshtml` ML Promotion Status Panel**
  - **File:** `MatchPredictor.Web/Pages/Analytics.cshtml#L152-L174`
  - **Enhancement:**
    - Display all 6 markets.
    - Show `Champion` badge for promoted models, `Shadow Challenger (N samples, p-val)` for active shadows, and `No Promoted Model` with last evaluated holdout delta for unpromoted markets.
    - In the `Isotonic Calibration Profiles` panel, display a small helper note explaining the $> +0.0025$ promotion threshold so users understand why models with positive delta below $0.0025$ remain in shadow mode.

---

### Phase 6: Automated Testing & Verification
**Goal:** Ensure 100% test coverage for all fixed components with zero regressions.

- [ ] **Task 6.1: Unit Tests for Totals Blending**
  - **File:** `MatchPredictor.Tests.Unit/DataAnalyzerServiceTotalsMlTests.cs`
  - Verify Under 2.5 ML signal alters output probability when promoted.
- [ ] **Task 6.2: Integration Tests for Challenger Training & DB Coexistence**
  - **File:** `MatchPredictor.Tests.Integration/MarketPredictionModelServiceShadowTests.cs`
  - Verify `TrainChallengerProfilesAsync` succeeds when champion already exists in database.
- [ ] **Task 6.3: Shadow Logging & Settlement End-to-End Test**
  - Verify shadow evaluations are created on prediction generation and settled on score updater.

---

## Risk Assessment & Rollback Plan

| Risk | Mitigation | Rollback Plan |
| :--- | :--- | :--- |
| **Migration lock on Neon DB** | Non-blocking `CREATE INDEX CONCURRENTLY` or brief maintenance window | Drop filtered indices and restore previous index |
| **Probability shift on Under 2.5** | Ensure Under 2.5 ML weight follows `EnsembleWeights` configuration | Set `EnsembleWeights.Ml = 0` via config if calibration drifts |
| **Overhead of shadow prediction** | Challenger model inference is in-memory LightGBM ($< 0.5\text{ ms}$) | Bypass challenger inference if CPU threshold exceeded |

---

## Agent Responsibilities

| Phase | Assigned Specialist | Role |
| :--- | :--- | :--- |
| Phase 1 | `database-architect` | EF Core migration, index optimization, PostgreSQL schema validation |
| Phase 2 & 4 | `backend-specialist` | Signal blending math, ensemble weight integration |
| Phase 3 | `orchestrator` / `debugger` | Shadow evaluation wiring and settlement integration |
| Phase 5 | `frontend-specialist` | Razor page diagnostics UI and status badges |
| Phase 6 | `test-engineer` | Integration tests and test suite execution |
