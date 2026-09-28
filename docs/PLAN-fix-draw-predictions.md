# Plan: Restore Draw Predictions by Correcting Publish Threshold

- **Slug:** `fix-draw-predictions`
- **Primary Agent:** `project-planner`
- **Collaborating Agents:** `backend-specialist`, `database-architect`, `test-engineer`
- **Target Files:**
  - `MatchPredictor.Domain/Models/PredictionSettings.cs`
  - `MatchPredictor.Web/appsettings.json`
  - `docs/PLAN-fix-draw-predictions.md`
- **Status:** DRAFTED & READY FOR EXECUTION

---

## 1. Root Cause Analysis

### The Empirical Findings:
1. **Historic Precedent**:
   - Up until September 17, 2026, the application published **10,561 Draw predictions** (averaging 60–100 draws published per day).
   - On September 15: **60** draws published.
   - On September 16: **99** draws published.
   - On September 17: **84** draws published.
   - On September 18: **0** draws published.
   - Every day from September 18 to present: **0** draws published.
2. **The Breaking Change**:
   - In commit `ec69509f` on September 17, 2026 at 19:38:38 (`"Enforce betslip quality gates and soft-suppress weak markets"`), `DrawStrongThreshold` was changed from `0.30` to `0.45` in both:
     - `MatchPredictor.Web/appsettings.json` (line 22)
     - `MatchPredictor.Domain/Models/PredictionSettings.cs` (line 10)
3. **Mathematical Impossibility in Soccer 1X2 Markets**:
   - In 3-way match outcome markets (Home, Draw, Away), the natural base rate for a draw is ~25%–28%.
   - Across **13,882 draw forecast observations** in the production database since September 18, the **maximum calibrated probability** was **0.3915** (39.15%), and the average draw probability was **0.24** (24%).
   - Because `DataAnalyzerService.SelectPublishedPredictions` enforces:
     ```csharp
     candidate.Market == PredictionMarket.Draw &&
     HasExplicitDrawMarket(candidate) &&
     candidate.CalibratedProbability >= candidate.ThresholdUsed // (0.45)
     ```
     **0.00%** of matches could ever reach 0.45.
4. **Impact on the Draw Page (`/Predictions/Draw`)**:
   - `PredictionQueries.GetDrawAsync(today)` queries the `Predictions` table for `PredictionCategory == "Draw"` and `MatchLocalDate == today`.
   - Because 0 draw candidates qualified for publishing, 0 rows are written to `Predictions` each day.
   - The Draw page displays its empty state: *"No draw predictions available for today yet."*

---

## 2. Proposed Solution

### A. Restore `DrawStrongThreshold` to `0.30`
- In `MatchPredictor.Domain/Models/PredictionSettings.cs`:
  - Set `DrawStrongThreshold = 0.30` (aligning with `ThresholdTuningService.DrawMinimumThreshold = 0.30`).
- In `MatchPredictor.Web/appsettings.json`:
  - Set `"DrawStrongThreshold": 0.30`.

### B. Trigger / Support Backfill for Today's Card
- Once the threshold is restored to `0.30`, the next scheduled analysis run (or an on-demand trigger) will automatically generate and publish ~30–60 draw predictions for today's fixtures that meet the $\ge 30\%$ probability threshold.

### C. Verification
- Verify via unit & integration tests that `DataAnalyzerService` and `ThresholdTuningService` correctly publish and evaluate draw predictions at the 0.30 threshold.
- Run `checklist.py` to ensure all core audits pass.

---

## 3. Step-by-Step Task Breakdown

### Phase 1: Configuration & Domain Settings Update
- [ ] Task 1.1: In `MatchPredictor.Domain/Models/PredictionSettings.cs`, update default `DrawStrongThreshold` to `0.30`.
- [ ] Task 1.2: In `MatchPredictor.Web/appsettings.json`, update `"DrawStrongThreshold": 0.30`.

### Phase 2: Verification & Test Execution
- [ ] Task 2.1: Run `dotnet test MatchPredictor.Tests.Unit/MatchPredictor.Tests.Unit.csproj`.
- [ ] Task 2.2: Run `dotnet test MatchPredictor.Tests.Integration/MatchPredictor.Tests.Integration.csproj --filter "FullyQualifiedName~DataAnalyzerServiceTests|FullyQualifiedName~ThresholdTuningServiceTests"`.
- [ ] Task 2.3: Run `python3 .agent/scripts/checklist.py .`.

### Phase 3: Deployment & Verification
- [ ] Task 3.1: Commit changes: `fix(predictions): restore DrawStrongThreshold to 0.30 to allow draw predictions to publish`.
- [ ] Task 3.2: Push to `origin/ai-plan`.
- [ ] Task 3.3: Verify that scheduled or manual prediction generation populates draw predictions on `/Predictions/Draw`.

---

## 4. Verification Criteria
- `DrawStrongThreshold` equals `0.30` across all configurations.
- All unit and targeted integration tests pass.
- `checklist.py` passes 6/6 core checks.
