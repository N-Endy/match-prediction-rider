# Plan: Advanced Prediction & Betting Architecture (Simultaneous Kelly, Lineup Modeling & Odds Drift)

- **Plan File:** `docs/PLAN-portfolio-lineups-odds.md`
- **Slug:** `portfolio-lineups-odds`
- **Project Type:** Full-Stack Sports Analytics (.NET 10 C# Solution: Domain, Application, Infrastructure, Web Razor Pages, Hangfire)
- **Primary Agent:** `project-planner`
- **Collaborating Agents:** `backend-specialist`, `frontend-specialist`, `test-engineer`
- **Key Skills:** `clean-code`, `database-design`, `api-patterns`, `frontend-design`, `testing-patterns`, `performance-profiling`
- **Status:** DRAFT - READY FOR EXECUTION

---

## 1. Executive Summary & Architectural Alignment

Following the mathematical audit and simplex calibration hardening of the MatchPredictor engine, this plan addresses three critical predictive and risk-management capabilities required to advance the solution into an institutional-grade sports quantitative forecasting platform:

1. **Portfolio & Risk Management (Simultaneous Multi-Bet Kelly Optimization):**
   - *Current Gap:* Fractional Kelly staking (`BetPricingMath.CalculateFractionalKellyStakeFraction`) treats each value bet in isolation ($0.25 f^*$). During peak Saturday slate windows (e.g. 15:00 UTC), 15–20 value bets kickoff concurrently. Unconstrained independent staking results in combined portfolio exposure exceeding 40%–60% of total bankroll, exposing the capital to ruinous simultaneous drawdowns.
   - *Architecture Tie-in:* Introduces `SimultaneousKellyOptimizer` in `MatchPredictor.Domain/Models/BetPricingMath.cs`. Clusters value bets by kickoff time windows, solves the constrained simultaneous Kelly allocation problem under an aggregate window risk ceiling ($\sum f_i \le C_{\text{max}}$, default 20% bankroll), and surfaces adjusted stakes in `ValueBetsService` and the UI.

2. **Lineup & Injury Availability Impact Modeling:**
   - *Current Gap:* Predictive features are formed 12–24 hours before kickoff from historical season aggregates. Late-breaking team news (top goalscorer benched, starting goalkeeper injured 60m pre-match) currently produces no adjustment to team xG ($\lambda, \mu$) or win probabilities.
   - *Architecture Tie-in:* Extends `ApplicationDbContext` with `MatchLineupSnapshot`. Introduces a lightweight 5-minute Hangfire poller (`LineupAvailabilityRefreshJob`) that monitors matches within $T-75\text{m}$. Confirmed Starting XIs modulate the Dixon-Coles team attack ($\alpha$) and defense ($\beta$) log-space parameters before Poisson score-matrix recalculation, logging all modifiers in `FeatureContributionsJson`.

3. **Odds Movement Velocity & Market Timing (Drift & Steam Move Alerts):**
   - *Current Gap:* Odds snapshots (`PredictionOddsSnapshotKind`) only capture `Publish` and `Close` (15m before kickoff). Mid-week and matchday price discovery trajectories are lost, leaving users uninformed as to whether odds are crashing (steaming) or lengthening (drifting).
   - *Architecture Tie-in:* Extends `PredictionOddsSnapshotKind` with `Opening = 3`, `Interim = 4`, and `Steam = 5`. Captures multi-point intraday odds curves via `MarketOddsSnapshot`. Implements `MarketTimingService` to compute odds velocity ($v = \frac{\Delta \text{odds}}{\Delta t}$), detect sharp syndicate steam moves vs. drifting prices, and render actionable market timing advisories ("⚡ Steam Move - Take Now", "📈 Drifting - Wait for Peak Price", "⚖️ Stable Price").

---

## 2. Success Criteria & Verification Metrics

- [ ] **Simultaneous Kelly Formulation:**
  - For any kickoff window with $N$ concurrent bets, aggregate Kelly exposure $\sum f_i$ strictly never exceeds `MaxConcurrentWindowExposureFraction` (default 20%).
  - Individual stakes are scaled via quadratic utility / KKT water-filling optimization such that higher-edge/lower-variance picks receive priority allocation.
  - Single-bet edge and standalone Kelly values remain preserved for reference alongside portfolio stakes.
- [ ] **Confirmed Lineup Impact Pipeline:**
  - `MatchLineupSnapshot` entity persists confirmed lineups, key player absences, and computed attack/defense modifiers ($\Delta \alpha, \Delta \beta$).
  - When confirmed starting XIs are detected at $T-75\text{m}$, Dixon-Coles expected goals ($\lambda, \mu$) are updated, regenerating market probabilities without violating simplex coherence ($P_H + P_D + P_A = 1.0$).
  - Predictions updated by confirmed lineups are flagged with `IsLineupConfirmed = true` and display the net xG delta in `FeatureContributionsJson`.
- [ ] **Market Timing & Odds Velocity Engine:**
  - Multi-snapshot odds tracking captures price points at $T-24\text{h}$, $T-6\text{h}$, $T-2\text{h}$, and $T-1\text{h}$.
  - Odds velocity $v_{\text{odds}}$ and de-vigged implied probability delta $\Delta p_{\text{mkt}}$ classify market state into `UrgentTakeNow` (steam drop $\ge 4\%$ in $\le 2\text{h}$), `DriftingWait` (price lengthening $\ge 0.15$), or `Stable`.
  - Advisory badges render seamlessly in `ValueBets.cshtml`, `Match/Index.cshtml`, and `_PredictionCard.cshtml`.
- [ ] **Regression & System Stability:**
  - 100% of existing unit tests pass (304+ tests).
  - All new mathematical modules and database migrations are covered by unit and integration tests.
  - Zero performance regressions in Hangfire background job executions.

---

## 3. Detailed Architecture & Technical Design

### A. Portfolio & Risk Management: Simultaneous Multi-Bet Kelly

#### 1. Mathematical Formulation
In classical portfolio theory for sports betting with independent events $i = 1, \dots, N$:
- Net odds: $b_i = O_i - 1$
- Model probability: $p_i$
- Standalone fractional Kelly:
  $$f_i^* = c_{\text{fraction}} \cdot \max\left(0, \frac{p_i b_i - (1 - p_i)}{b_i}\right)$$
- Expected excess return: $\mu_i = p_i b_i - (1 - p_i)$
- Variance proxy: $\sigma_i^2 = p_i (1 - p_i) b_i^2 + (1 - p_i) \approx b_i$

When multiple bets share the same kickoff time window $W$ (matches kicking off within $\pm 45$ minutes of each other):
If the unconstrained sum exceeds the maximum window exposure limit $C_{\text{max}}$ (e.g. 0.20 of bankroll):
$$\sum_{i \in W} f_i^* > C_{\text{max}}$$

The simultaneous optimization solves the constrained quadratic risk allocation:
$$\max_{\{f_i\}} \sum_{i \in W} \left( \mu_i f_i - \frac{1}{2} \gamma \sigma_i^2 f_i^2 \right) \quad \text{subject to} \quad \sum_{i \in W} f_i \le C_{\text{max}}, \quad 0 \le f_i \le f_i^*$$

Using the Karush-Kuhn-Tucker (KKT) water-filling algorithm with Lagrange multiplier $\lambda \ge 0$:
$$f_i(\lambda) = \max\left(0, \min\left(f_i^*, \frac{\mu_i - \lambda}{\gamma \sigma_i^2}\right)\right)$$
where $\lambda$ is solved via bisection search such that $\sum_{i \in W} f_i(\lambda) = C_{\text{max}}$. This guarantees:
1. Picks with marginal edge are pruned first.
2. High-edge, high-conviction picks receive proportional capital.
3. Total bankroll at risk in any single kickoff window is strictly capped.

#### 2. Codebase Implementation
- **File:** `MatchPredictor.Domain/Models/BetPricingMath.cs`
  - Add method `OptimizeSimultaneousKellyStakes(IReadOnlyList<SimultaneousKellyCandidate> candidates, SimultaneousKellyOptions options)`
  - Add records:
    ```csharp
    public sealed record SimultaneousKellyCandidate(
        string CandidateKey,
        double ModelProbability,
        double DecimalOdds,
        DateTime KickoffUtc,
        double StandaloneKellyStakeFraction);

    public sealed record SimultaneousKellyOptions(
        double MaxWindowExposureFraction = 0.20,
        double WindowToleranceMinutes = 45.0,
        double KellyFraction = 0.25);

    public sealed record SimultaneousKellyAllocation(
        string CandidateKey,
        double StandaloneStakeFraction,
        double PortfolioStakeFraction,
        int WindowConcurrentBetCount,
        double WindowTotalExposureFraction,
        bool WasCapped);
    ```
- **File:** `MatchPredictor.Domain/Models/BetslipSettings.cs`
  - Add `MaxConcurrentWindowExposureFraction { get; set; } = 0.20;`
  - Add `ConcurrentWindowToleranceMinutes { get; set; } = 45;`
- **File:** `MatchPredictor.Application/Services/ValueBetsService.cs`
  - In `BuildCandidateReportAsync`, cluster `topCandidates` by `MatchDateTimeUtc` within 45-minute rolling windows.
  - Run `BetPricingMath.OptimizeSimultaneousKellyStakes` across each cluster.
  - Populate `KellyStakeFraction` (portfolio-adjusted) and `StandaloneKellyStakeFraction` on `ValueBetCandidate`.
- **UI:** In `ValueBets.cshtml`, render both stakes:
  - e.g., **"Rec. Stake: 1.8%"** with tooltip: *"Scaled from 3.2% due to 6 concurrent 15:00 kickoff bets (Window cap: 20%)"*.

---

### B. Lineup & Injury Availability Impact Modeling

#### 1. Mathematical Formulation
In `DixonColesModel.cs`, expected goals $(\lambda, \mu)$ are determined by:
$$\log \lambda = \text{Intercept} + \text{HomeAdvantage} + \alpha_{\text{home}} - \beta_{\text{away}}$$
$$\log \mu = \text{Intercept} + \alpha_{\text{away}} - \beta_{\text{home}}$$

When confirmed lineups are announced 60–75 minutes prior to kickoff, player availability deltas are calculated:
- **Starting XI Composition:** Match confirmed 11 players against the team's primary starter roster.
- **Player Importance Weighting ($w_k$):**
  - Star Striker / Primary Goal Threat (top 20% team xG contribution): $w_{\text{att}} \approx 0.12 - 0.20$
  - Starting Goalkeeper / Center-Back Captain: $w_{\text{def}} \approx 0.10 - 0.18$
  - Regular Rotational Starter: $w \approx 0.03 - 0.05$
- **Net Availability Modifiers:**
  $$\Delta \alpha_{\text{team}} = -\sum_{k \in \text{MissingStarters}} w_{k, \text{att}} + \sum_{j \in \text{KeyReturns}} w_{j, \text{att}}$$
  $$\Delta \beta_{\text{team}} = +\sum_{k \in \text{MissingDefenders}} w_{k, \text{def}} \quad (\text{positive } \beta \text{ means worse defense, conceding more})$$

Adjusted expected goals:
$$\log \lambda_{\text{adj}} = \log \lambda_{\text{base}} + \Delta \alpha_{\text{home}} + \Delta \beta_{\text{away}}$$
$$\log \mu_{\text{adj}} = \log \mu_{\text{base}} + \Delta \alpha_{\text{away}} + \Delta \beta_{\text{home}}$$

The adjusted $(\lambda_{\text{adj}}, \mu_{\text{adj}})$ generate an updated bivariate score matrix $\tau(h, a, \lambda, \mu, \rho)$, recalculating all probabilities with simplex normalization.

#### 2. Entity & Database Schema
- **Entity:** `MatchPredictor.Domain/Models/MatchLineupSnapshot.cs`
  ```csharp
  public class MatchLineupSnapshot
  {
      public int Id { get; set; }
      public string FixtureKey { get; set; } = string.Empty;
      public DateOnly MatchLocalDate { get; set; }
      public DateTime? MatchDateTimeUtc { get; set; }
      public string League { get; set; } = string.Empty;
      public string HomeTeam { get; set; } = string.Empty;
      public string AwayTeam { get; set; } = string.Empty;
      public bool IsConfirmed { get; set; }
      public string HomeStartingXiJson { get; set; } = "[]";
      public string AwayStartingXiJson { get; set; } = "[]";
      public string HomeAbsencesJson { get; set; } = "[]";
      public string AwayAbsencesJson { get; set; } = "[]";
      public double HomeAttackAdjustment { get; set; }
      public double HomeDefenseAdjustment { get; set; }
      public double AwayAttackAdjustment { get; set; }
      public double AwayDefenseAdjustment { get; set; }
      public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
  }
  ```
- **DbContext:** Register `DbSet<MatchLineupSnapshot> MatchLineupSnapshots` in `ApplicationDbContext.cs` with indices on `(FixtureKey, CapturedAtUtc)` and `(MatchLocalDate, IsConfirmed)`.
- **Migration:** `AddMatchLineupSnapshots`.

#### 3. Scraping & Pipeline Integration
- **Scraping Engine:**
  - In `WebScraperService.cs`, add `FetchLineupsForFixtureAsync(string homeTeam, string awayTeam, DateTime matchUtc)` leveraging SofaScore/FlashScore event lineup endpoints.
- **Hangfire Recurring Job:**
  - Add `lineup-refresh-job` in `HangfireRecurringJobs.cs` running every 5 minutes (`*/5 * * * *`).
  - Queries active predictions with $T-75\text{m} \le \text{MatchDateTime} \le T$.
  - Ingests confirmed lineups, computes $\Delta \alpha, \Delta \beta$, creates new current revision in `Predictions`, and flags `IsLineupConfirmed = true`.
- **Explainability:**
  - Store lineup adjustment details directly in `FeatureContributionsJson` (e.g. `{"lineupAdjusted": true, "homeNetXgDelta": -0.22, "missingStarters": ["Haaland"]}`).

---

### C. Odds Movement Velocity & Market Timing (Drift & Steam Move Alerts)

#### 1. Mathematical Formulation & Signal Detection
Let decimal odds for outcome $k$ at time $t$ be $O_k(t)$.
- Time delta: $\Delta t = t_2 - t_1$ (in hours)
- Odds drift velocity:
  $$v_{\text{odds}} = \frac{O_k(t_2) - O_k(t_1)}{\Delta t}$$
- De-vigged market probability change:
  $$\Delta P_{\text{fair}} = P_{\text{fair}}(t_2) - P_{\text{fair}}(t_1)$$

#### Signal Classification Matrix:
| Signal Category | Criteria | Advisory State | User Guidance & UI Badge |
|---|---|---|---|
| **Steam Move (Sharp Action)** | $\Delta P_{\text{fair}} \ge +0.035$ and $\Delta t \le 2\text{h}$ | `UrgentTakeNow` | ⚡ **Steam Move: Price Dropping Rapidly** — Take current line before edge evaporates. |
| **Drifting Price** | $O(t_2) \ge O(t_1) + 0.15$ with $v_{\text{odds}} > 0.05/\text{h}$ | `DriftingWait` | 📈 **Price Drifting Upward** — Value expanding; consider waiting for peak price closer to kickoff. |
| **Reverse Line Movement (RLM)**| Public volume heavy on Side A, but Side B odds shortening | `SharpContrarian` | 🎯 **Sharp Reverse Movement** — Syndicate capital diverging from public consensus. |
| **Stable Line** | $|\Delta P_{\text{fair}}| < 0.015$ across $> 6\text{h}$ | `Stable` | ⚖️ **Stable Market Price** — Line is mature and efficient; bet at convenience. |

#### 2. Domain & Application Architecture
- **Enum Extension:**
  In `MatchPredictor.Domain/Models/PredictionOddsSnapshotKind.cs`:
  ```csharp
  public enum PredictionOddsSnapshotKind
  {
      Publish = 1,
      Close = 2,
      Opening = 3,
      Interim = 4,
      Steam = 5
  }
  ```
- **Service:**
  `MatchPredictor.Application/Services/MarketTimingService.cs`:
  - Implements `IMarketTimingService`.
  - Methods:
    - `Task<MarketTimingAdvisory> AnalyzeMarketTimingAsync(int predictionId, CancellationToken ct)`
    - `Task CaptureInterimOddsSnapshotsAsync(CancellationToken ct)`
- **Background Snapshot Polling:**
  - Update `AnalyzerService.CaptureClosingLineSnapshotsAsync` or add `interim-odds-snapshot-job` in `HangfireRecurringJobs.cs` running every 15 minutes (`*/15 * * * *`).
  - Takes snapshots of upcoming matches at key checkpoints ($T-24\text{h}$, $T-6\text{h}$, $T-2\text{h}$, $T-1\text{h}$), storing snapshots in `MarketOddsSnapshot` and `PredictionOddsSnapshot`.
- **UI Integration:**
  - Render market timing badge in `_PredictionCard.cshtml` and `ValueBets.cshtml`:
    ```html
    @if (candidate.TimingSignal == TimingSignal.UrgentTakeNow)
    {
        <span class="mp-badge mp-badge-urgent" title="Odds shortening rapidly (-0.25 in 90m)">⚡ Take Now</span>
    }
    else if (candidate.TimingSignal == TimingSignal.DriftingWait)
    {
        <span class="mp-badge mp-badge-info" title="Odds drifting out (+0.18 today)">📈 Drifting</span>
    }
    ```

---

## 4. Phased Implementation Roadmap

```mermaid
flowchart TD
    subgraph Phase 1: Portfolio Kelly Staking
        P1_1[BetPricingMath: Simultaneous Kelly Solver] --> P1_2[BetslipSettings: Window Risk Bounds]
        P1_2 --> P1_3[ValueBetsService: Window Clustering]
        P1_3 --> P1_4[UI: ValueBets.cshtml Portfolio Sizing Badges]
    end

    subgraph Phase 2: Lineup & Injury Engine
        P2_1[Domain Entity: MatchLineupSnapshot] --> P2_2[EF Core Migration: AddMatchLineupSnapshots]
        P2_2 --> P2_3[WebScraperService: Lineup Extraction]
        P2_3 --> P2_4[DixonColesModel: Attack/Defense Delta Modifiers]
        P2_4 --> P2_5[Hangfire: LineupAvailabilityRefreshJob]
    end

    subgraph Phase 3: Odds Drift & Market Timing
        P3_1[Domain Enum: PredictionOddsSnapshotKind Extensions] --> P3_2[MarketTimingService: Velocity & Steam Detection]
        P3_2 --> P3_3[Hangfire: Multi-Point Odds Snapshot Polling]
        P3_3 --> P3_4[UI: Market Timing Advisory Badges]
    end

    subgraph Phase 4: Verification & Integration
        P4_1[Unit Tests: SimultaneousKellyTests] --> P4_4[Full Regression Suite]
        P4_2[Unit Tests: LineupAdjustmentTests] --> P4_4
        P4_3[Unit Tests: OddsVelocityTests] --> P4_4
        P4_4 --> P4_5[Checklist.py Audit Pass]
    end

    Phase 1 --> Phase 4
    Phase 2 --> Phase 4
    Phase 3 --> Phase 4
```

---

### Phase 1: Portfolio & Risk Management (Simultaneous Kelly)
- [ ] **Task 1.1:** Add `SimultaneousKellyOptimizer` and mathematical records in `MatchPredictor.Domain/Models/BetPricingMath.cs`.
- [ ] **Task 1.2:** Add `MaxConcurrentWindowExposureFraction` and `ConcurrentWindowToleranceMinutes` in `MatchPredictor.Domain/Models/BetslipSettings.cs`.
- [ ] **Task 1.3:** Update `ValueBetsService.cs` to cluster candidate bets by kickoff window and apply portfolio optimization.
- [ ] **Task 1.4:** Update `ValueBetCandidate.cs` and `ValueBetsViewModel.cs` with portfolio stake fractions and window bet counts.
- [ ] **Task 1.5:** Update `ValueBets.cshtml` and `_BetslipDrawer.cshtml` to display portfolio-adjusted stake units and exposure warnings.
- [ ] **Task 1.6:** Add unit tests in `MatchPredictor.Tests.Unit/SimultaneousKellyOptimizerTests.cs` verifying simplex allocation, exposure clamping, and edge prioritization.

### Phase 2: Lineup & Injury Availability Impact Modeling
- [ ] **Task 2.1:** Create `MatchLineupSnapshot.cs` domain entity in `MatchPredictor.Domain/Models/`.
- [ ] **Task 2.2:** Add `DbSet<MatchLineupSnapshot>` and EF Core entity configuration in `ApplicationDbContext.cs`.
- [ ] **Task 2.3:** Generate EF Core migration `AddMatchLineupSnapshots` in `MatchPredictor.Infrastructure`.
- [ ] **Task 2.4:** Extend `DixonColesModel.cs` with `ExpectedGoalsWithModifiers(homeTeam, awayTeam, homeAttMod, homeDefMod, awayAttMod, awayDefMod)`.
- [ ] **Task 2.5:** Implement lineup extraction methods in `WebScraperService.cs` (SofaScore / FlashScore confirmed XI parsers).
- [ ] **Task 2.6:** Create `LineupAvailabilityRefreshJob` in `MatchPredictor.Application/Services/` and register in `HangfireRecurringJobs.cs` (every 5m).
- [ ] **Task 2.7:** Update `DataAnalyzerService.cs` to apply lineup availability modifiers when confirmed snapshots exist, persisting adjustments in `FeatureContributionsJson`.
- [ ] **Task 2.8:** Add unit tests in `MatchPredictor.Tests.Unit/LineupAdjustmentTests.cs` verifying xG deltas and simplex preservation.

### Phase 3: Odds Movement Velocity & Market Timing
- [ ] **Task 3.1:** Extend `PredictionOddsSnapshotKind` with `Opening`, `Interim`, and `Steam` in `MatchPredictor.Domain/Models/PredictionOddsSnapshotKind.cs`.
- [ ] **Task 3.2:** Create `MarketTimingAdvisory.cs` domain model in `MatchPredictor.Domain/Models/`.
- [ ] **Task 3.3:** Implement `MarketTimingService.cs` in `MatchPredictor.Application/Services/` to compute odds velocity and detect steam/drift signals.
- [ ] **Task 3.4:** Add periodic intraday odds snapshot capture in `AnalyzerService.cs` and register `interim-odds-snapshot-job` in `HangfireRecurringJobs.cs` (every 15m).
- [ ] **Task 3.5:** Update `ValueBetsService.cs` and match page view models to include `MarketTimingAdvisory`.
- [ ] **Task 3.6:** Update `ValueBets.cshtml`, `Match/Index.cshtml`, and `_PredictionCard.cshtml` with visual timing badges (`⚡ Steam Move`, `📈 Drifting`, `⚖️ Stable`).
- [ ] **Task 3.7:** Add unit tests in `MatchPredictor.Tests.Unit/MarketTimingServiceTests.cs` verifying velocity calculations, noise filtering, and signal classification.

### Phase 4: Full System Verification & Audit
- [ ] **Task 4.1:** Run unit test suite: `dotnet test MatchPredictor.Tests.Unit` (all 304+ tests plus new tests passing).
- [ ] **Task 4.2:** Run integration tests: `dotnet test MatchPredictor.Tests.Integration`.
- [ ] **Task 4.3:** Run comprehensive code quality and security audit: `python .agent/scripts/checklist.py .`.
- [ ] **Task 4.4:** Verify database migration execution against test database context.

---

## 5. Potential Risks & Mitigation Strategies

| Risk | Impact | Mitigation Strategy |
|---|---|---|
| **Lineup Scraper Availability / Flakiness** | High | Implement graceful fallback: if confirmed lineups are delayed or unavailable, the engine uses the baseline seasonal rating without failing the prediction run. |
| **Lineup Adjustment Overreaction** | Medium | Clamp net attack/defense modifiers ($\Delta \alpha \in [-0.25, +0.25]$) to prevent extreme outliers from distorting Poisson parameters. |
| **High Frequency Odds Snapshot Database Growth** | Medium | Retention policy: Interim odds snapshots for settled matches are compressed or pruned after 30 days via `cleanup-old-predictions` job, keeping only `Opening`, `Close`, and key `Steam` inflection points. |
| **Simultaneous Kelly Over-Pruning** | Low | Provide both unconstrained standalone Kelly and portfolio-adjusted Kelly in the UI so users have transparent visibility into the risk reduction rationale. |

---

## 6. Verification Commands

Upon execution of the phases, verification will be validated via:

```bash
# 1. Run unit test suite
dotnet test MatchPredictor.Tests.Unit

# 2. Run integration test suite
dotnet test MatchPredictor.Tests.Integration

# 3. Master project checklist audit
python .agent/scripts/checklist.py .
```
