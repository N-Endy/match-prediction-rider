# Plan: MLOps Continuous Retraining Architecture & Correlated Multi-Leg Slip Modeling

- **Plan File:** `docs/PLAN-mlops-copula-sgm.md`
- **Slug:** `mlops-copula-sgm`
- **Project Type:** Full-Stack Sports Analytics (.NET 10 C# Solution: Domain, Application, Infrastructure, Web Razor Pages, Hangfire)
- **Primary Agent:** `project-planner`
- **Collaborating Agents:** `backend-specialist`, `database-architect`, `test-engineer`
- **Key Skills:** `clean-code`, `database-design`, `api-patterns`, `testing-patterns`, `performance-profiling`, `brainstorming`
- **Status:** DRAFT - READY FOR ARCHITECTURAL REVIEW & EXECUTION

---

## 1. Executive Summary & Architectural Alignment

Building on the hardened simplex normalization, simultaneous multi-bet Kelly optimization, confirmed lineup modeling, and odds velocity engines, this plan establishes the next generation of MatchPredictor's predictive intelligence across two advanced vectors:

1. **MLOps & Automated Retraining Architecture (Feature Drift & Champion/Challenger Shadowing):**
   - *Current Gap:* LightGBM models (`MarketPredictionModelService`) are trained on fixed historical holdouts with basic Brier improvement gates. The system does not detect covariate or concept drift as leagues evolve (e.g. winter fixture congestion, rule modifications, or VAR stoppage time extensions). Models are updated manually without continuous out-of-sample shadow verification on live fixtures or hypothesis testing ($p < 0.05$).
   - *Architecture Tie-in:* Introduces `FeatureDriftMonitorService` computing Population Stability Index (PSI) and 1D Wasserstein distance across rolling feature windows. Deploys a weekly Hangfire pipeline (`ChallengerTrainingJob`) running candidate models in non-blocking **Shadow Mode** on live fixtures. Introduces a statistically rigorous promotion gate using the paired Wilcoxon Signed-Rank Test on out-of-sample Brier score and log-loss before automated champion promotion.

2. **Correlated Slip & Accumulator Modeling (Beyond Independent Multiplication):**
   - *Current Gap 1 (Cross-Match Accas):* Accumulator packing (`WeekendPayoutSlipComposer`) computes total slip probability as independent multiplication $P = \prod_{i=1}^M p_i$. In real-world football, systemic league co-movements (simultaneous final-round motivation, cross-country storm weather suppressing goals, regional referee strictness) induce tail dependence that causes classic accumulators to misestimate tail risk and compound errors.
   - *Architecture Tie-in 1:* Implements an empirical Gaussian/Student-t Copula engine (`CopulaAccumulatorPricingService`) to estimate cross-match dependence and derive correlation-adjusted fair accumulator probabilities.
   - *Current Gap 2 (Same-Game Multi / Bet Builder):* The betslip generator strictly forbids selecting multiple markets from the same fixture. It has no mechanism to price compound intra-match bets (e.g. "Home Win + BTTS + Over 2.5").
   - *Architecture Tie-in 2:* Implements `SameGameMultiService` leveraging the calibrated Dixon-Coles bivariate Poisson score probability tensor $P(X = x, Y = y)$. Calculates exact joint probability mass by intersecting outcome predicates over the $10 \times 10$ score grid, unlocking mathematically sound, high-EV Bet Builder slips.

---

## 2. Success Criteria & Verification Metrics

- [ ] **Data & Concept Drift Monitoring:**
  - `FeatureDriftMonitorService` computes Population Stability Index (PSI) and Wasserstein metric for core features (e.g. `HomeXG`, `AwayXG`, `RollingGoalAverage`, `RestDaysDifference`) comparing the current 30-day window against baseline training reference.
  - Automatically flags drift warnings when $\text{PSI} \ge 0.10$ and triggers automated retraining recommendations when $\text{PSI} \ge 0.25$.
- [ ] **Champion / Challenger Shadow Evaluation & Promotion Gate:**
  - Automated weekly training persists candidate LightGBM models tagged as `Challenger` in `MarketMlModelProfile`.
  - For all published predictions, both Champion and Challenger probabilities are computed and logged in `ModelShadowEvaluation`.
  - Champion promotion gate runs after $N \ge 100$ settled shadow predictions: promotion occurs **only** if the challenger achieves lower Brier score and lower log-loss with statistical significance ($p < 0.05$ via Wilcoxon Signed-Rank Test).
- [ ] **Cross-Match Copula Correlation for Accumulators:**
  - `CopulaAccumulatorPricingService` accepts an accumulator leg collection and computes the copula-adjusted joint probability $P_{\text{copula}}(\text{Acca})$ using empirical league/window correlation matrix $\Sigma$.
  - Adjusts expected value (EV) and tail-risk warnings on large accumulators (6+ legs) where naive independence underestimates correlation breakdown.
- [ ] **Same-Game Multi (Bet Builder) Exact Joint Pricing:**
  - `SameGameMultiService` calculates the exact joint probability of intra-match combinations (e.g. 1X2 + O/U + BTTS) directly from the bivariate Dixon-Coles score grid without naive multiplication.
  - Guarantees probabilistic consistency: $P(\text{SGM}) \le \min_i(P(\text{Leg}_i))$ and $P(\text{Mutually Exclusive Combo}) = 0.0$.
  - A new Bet Builder composer creates curated, high-edge Same-Game Multi slips for top weekend fixtures.
- [ ] **Regression & Test Coverage:**
  - 100% of existing unit tests (312+) and integration tests (434+) pass with zero regressions.
  - Comprehensive unit test suites created for PSI/Wasserstein drift math, Wilcoxon promotion tests, Gaussian copula sampling, and Dixon-Coles SGM joint grid summation.

---

## 3. Detailed Architecture & Technical Design

### A. MLOps Architecture: Feature Drift & Shadow Retraining

```
[Settled Fixtures & Features] 
             │
             ▼
   [FeatureDriftMonitor] ──> Computes PSI & Wasserstein Distance ──> Drift Alert Dashboard
             │
   (Weekly Hangfire Job)
             │
             ▼
  [Challenger Model Training] ──> Persists Challenger Model (Shadow Status)
             │
             ▼
   [Live Matchday Ingestion] ──> Generates: Champion Forecast (Production)
                                            Challenger Forecast (Shadow Log)
             │
             ▼
[Settled Shadow Forecasts (N >= 100)] 
             │
             ▼
[Paired Wilcoxon Signed-Rank Test] ──> p < 0.05 & Brier_challenger < Brier_champion?
             │
             ├── Yes ──> Auto-Promote Challenger to Production Champion
             └── No  ──> Retain Current Champion; Log Evaluation Diagnostics
```

#### 1. Mathematical Formulations for Drift & Evaluation
- **Population Stability Index (PSI):**
  For continuous feature values partitioned into $K = 10$ quantile bins from reference training set $Q$:
  $$\text{PSI} = \sum_{k=1}^K \left( P_k - Q_k \right) \times \ln\left(\frac{P_k + \epsilon}{Q_k + \epsilon}\right)$$
  - $\text{PSI} < 0.10$: Stable distribution (no action required).
  - $0.10 \le \text{PSI} < 0.25$: Moderate drift (warning surfaced in system health dashboard).
  - $\text{PSI} \ge 0.25$: Significant covariate shift (triggers retraining flag).

- **Wasserstein Distance ($W_1$ Metric / Earth Mover's Distance):**
  For continuous distributions $P$ and $Q$ with empirical cumulative distribution functions $F_P$ and $F_Q$:
  $$W_1(P, Q) = \int_{-\infty}^\infty |F_P(x) - F_Q(x)| \, dx = \frac{1}{n}\sum_{i=1}^n |x_{(i)} - y_{(i)}|$$
  Provides robust, bin-free geometric distance between the reference training feature distribution and live matchday features.

- **Paired Wilcoxon Signed-Rank Hypothesis Test:**
  For $N$ paired out-of-sample Brier loss differentials $d_i = \text{Loss}_{\text{champion}, i} - \text{Loss}_{\text{challenger}, i}$:
  - Rank absolute differences $|d_i|$, discarding pairs where $d_i = 0$.
  - Sum positive ranks $W^+ = \sum_{d_i > 0} R_i$.
  - For $N \ge 25$, test statistic $Z = \frac{W^+ - \frac{N(N+1)}{4}}{\sqrt{\frac{N(N+1)(2N+1)}{24}}}$ evaluates one-tailed $p$-value for $H_1: \text{Loss}_{\text{challenger}} < \text{Loss}_{\text{champion}}$.
  - Promotion executes automatically if and only if $p < 0.05$ and sample size $N \ge 100$.

#### 2. Persistence & Entities
- **New Entity: `ModelShadowEvaluation`**
  - `Id` (long, PK)
  - `PredictionId` (int, FK)
  - `Market` (`PredictionMarket`)
  - `ChampionCalibratedProbability` (double)
  - `ChallengerCalibratedProbability` (double)
  - `OutcomeOccurred` (bool?)
  - `ChampionBrierLoss` (double?)
  - `ChallengerBrierLoss` (double?)
  - `CapturedAtUtc` (DateTime)
  - `SettledAtUtc` (DateTime?)
- **Extension to `MarketMlModelProfile`:**
  - `IsShadow` (bool)
  - `ShadowSampleCount` (int)
  - `ShadowBrierScore` (double?)
  - `ShadowLogLoss` (double?)
  - `PromotionPValue` (double?)

---

### B. Correlated Slip & Accumulator Modeling: Copulas & Same-Game Multi

```
                                [Betslip Generation]
                                         │
                 ┌───────────────────────┴───────────────────────┐
                 ▼                                               ▼
    [Cross-Match Accumulators]                      [Same-Game Multi / Bet Builder]
                 │                                               │
   [Gaussian Copula Engine]                         [Dixon-Coles Score Grid Engine]
                 │                                               │
  • Models equicorrelation rho                     • Bivariate Poisson matrix P(x, y)
  • Joint tail-risk probability                    • Intersect compound predicates
  • Correlation-adjusted EV%                       • Exact joint probability & Fair Odds
```

#### 1. Cross-Match Accumulators: Gaussian Copula Engine
When accumulator legs $i = 1, \dots, M$ share subtle positive correlation (e.g. high-wind storms depressing goal totals across UK matches, or synchronous final-day attacking aggression across a league), the independent probability $P = \prod p_i$ under-represents variance and tail risk.

Under a Gaussian Copula with correlation matrix $\Sigma \in \mathbb{R}^{M \times M}$:
$$P(E_1 = 1, \dots, E_M = 1) = \Phi_\Sigma\left( \Phi^{-1}(p_1), \dots, \Phi^{-1}(p_M) \right)$$
where $\Phi^{-1}$ is the standard normal inverse cumulative distribution function (probit).
- For equicorrelated legs sharing a league or regional weather environment with correlation $\rho$:
  $$Z_i = \sqrt{1 - \rho} \, X_i + \sqrt{\rho} \, Z_0, \quad X_i, Z_0 \sim \mathcal{N}(0, 1)$$
- The copula probability is computed via fast Gauss-Hermite numerical integration:
  $$P(\text{Acca}) = \int_{-\infty}^\infty \left[ \prod_{i=1}^M \Phi\left( \frac{\Phi^{-1}(p_i) - \sqrt{\rho} \, z_0}{\sqrt{1 - \rho}} \right) \right] \phi(z_0) \, dz_0$$
- This yields closed-form or single-dimensional quadrature computation with $< 0.1\text{ms}$ execution latency per slip.

#### 2. Same-Game Multi (Bet Builder): Dixon-Coles Bivariate Score Grid Integration
For a single fixture $F$, Dixon-Coles computes the score probabilities $P(X = x, Y = y)$ for all scorelines $(x, y) \in \{0, \dots, 9\}^2$, with low-score $\tau(x, y; \rho)$ interaction:
$$\sum_{x=0}^9 \sum_{y=0}^9 P(X = x, Y = y) = 1.0$$

Any intra-match parlay consists of $K$ market conditions $C_1, \dots, C_K$.
Each condition maps to a boolean predicate on $(x, y)$:
- `Home Win`: $x > y$
- `Draw`: $x = y$
- `Away Win`: $x < y$
- `Over 2.5`: $x + y \ge 3$
- `Under 2.5`: $x + y \le 2$
- `BTTS (Yes)`: $x \ge 1 \land y \ge 1$
- `BTTS (No)`: $x = 0 \lor y = 0$
- `Home Clean Sheet`: $y = 0$
- `Team To Score 2+`: $x \ge 2$

The exact joint probability of the Bet Builder combo is given by the sum of joint mass over the intersection:
$$\mathcal{S}_{\text{combo}} = \left\{ (x, y) \in \{0, \dots, 9\}^2 \mid \bigwedge_{k=1}^K C_k(x, y) = \text{true} \right\}$$
$$P_{\text{SGM}} = \sum_{(x, y) \in \mathcal{S}_{\text{combo}}} P(X = x, Y = y)$$

**Key Invariants:**
1. Mutually contradictory legs (e.g. `Home Win` and `Under 0.5 Goals`) yield $\mathcal{S}_{\text{combo}} = \emptyset \implies P_{\text{SGM}} = 0.0$.
2. The joint probability strictly respects monotonicity: $P_{\text{SGM}} \le \min_{k} P(C_k)$.
3. The true correlation between markets (e.g. Home Win and Over 2.5 having strong positive correlation for heavy favorites) is naturally and rigorously captured without arbitrary correlation fudge factors.

---

## 4. Step-by-Step Implementation Breakdown

### Phase 1: MLOps Drift & Shadow Retraining Pipeline
- [ ] **Task 1.1: Statistical Drift Calculator (`FeatureDriftMath.cs`)**
  - **Agent:** `backend-specialist` | **Skills:** `clean-code`, `testing-patterns`
  - Implement quantile binning, Population Stability Index (PSI), and 1D Wasserstein distance algorithms.
  - *INPUT:* Reference feature array, Current feature array.
  - *OUTPUT:* PSI value, Wasserstein distance, drift severity level (`None`, `Moderate`, `Significant`).
  - *VERIFY:* Unit tests in `FeatureDriftMathTests.cs` against known distribution shift scenarios.

- [ ] **Task 1.2: Feature Drift Monitoring Service (`FeatureDriftMonitorService.cs`)**
  - **Agent:** `backend-specialist` | **Skills:** `database-design`, `clean-code`
  - Query 30-day rolling feature distributions from `ForecastObservations` / feature snapshots; compare against baseline training metrics.
  - Log drift metrics to database and emit structured warnings.
  - *INPUT:* Rolling window period, feature list.
  - *OUTPUT:* `FeatureDriftReport` DTO.
  - *VERIFY:* Integration test asserting drift calculation across simulated feature series.

- [ ] **Task 1.3: Shadow Evaluation Persistence & Entities**
  - **Agent:** `database-architect` | **Skills:** `database-design`
  - Create `ModelShadowEvaluation` entity; add `IsShadow`, `ShadowBrierScore`, `PromotionPValue` to `MarketMlModelProfile`.
  - Add EF Core DbSets and indexes in `ApplicationDbContext`.
  - *INPUT:* Entity definitions.
  - *OUTPUT:* EF Core mappings and schema update.
  - *VERIFY:* In-memory DbContext loads, saves, and queries shadow records cleanly.

- [ ] **Task 1.4: Statistical Promotion Gate (`ModelPromotionGate.cs`)**
  - **Agent:** `backend-specialist` | **Skills:** `clean-code`, `testing-patterns`
  - Implement paired Wilcoxon Signed-Rank Test and paired t-test for loss differentials.
  - Gate requires $N \ge 100$, $\Delta \text{Brier} > 0$, $\Delta \text{LogLoss} > 0$, and $p < 0.05$.
  - *INPUT:* Paired loss observations.
  - *OUTPUT:* `PromotionGateResult` (ShouldPromote, PValue, TestStatistic, SampleCount).
  - *VERIFY:* Unit tests in `ModelPromotionGateTests.cs` with synthetic paired differences.

- [ ] **Task 1.5: Challenger Training & Shadow Prediction Hangfire Jobs**
  - **Agent:** `backend-specialist` | **Skills:** `server-management`, `clean-code`
  - Register `challenger-training-job` (weekly) and `shadow-evaluation-settle-job` (nightly) in `HangfireRecurringJobs.cs`.
  - Extend prediction pipeline to evaluate challenger models in shadow mode without impacting published production forecasts.
  - *INPUT:* Published predictions and settled match results.
  - *OUTPUT:* Updated shadow evaluation rows and automated promotions when gate is cleared.
  - *VERIFY:* Integration test verifying shadow execution and promotion triggering.

---

### Phase 2: Correlated Accas & Same-Game Multi Modeling
- [ ] **Task 2.1: Gaussian Copula Tail-Risk Engine (`GaussianCopulaAccumulatorMath.cs`)**
  - **Agent:** `backend-specialist` | **Skills:** `clean-code`, `testing-patterns`
  - Implement Gaussian equicorrelation copula quadrature integration for multi-leg accumulators.
  - *INPUT:* Leg probabilities $[p_1, \dots, p_M]$, correlation coefficient $\rho$.
  - *OUTPUT:* Joint copula probability $P_{\text{copula}}$, probability ratio $P_{\text{copula}} / P_{\text{indep}}$, and tail-risk multiplier.
  - *VERIFY:* Unit tests in `GaussianCopulaAccumulatorMathTests.cs` verifying probability reduction under positive correlation and convergence to independence when $\rho = 0$.

- [ ] **Task 2.2: Copula-Adjusted Accumulator Composer Integration**
  - **Agent:** `backend-specialist` | **Skills:** `clean-code`
  - Integrate copula pricing into `WeekendPayoutSlipComposer` and `BetslipGenerationService`.
  - Flag slips with elevated correlation clustering (e.g. 4+ legs from the same league or weather region) with adjusted joint probability and realistic EV%.
  - *INPUT:* Candidate slip selections.
  - *OUTPUT:* Betslip entity with `CorrelationAdjustmentFactor` and `AdjustedCombinedOdds`.
  - *VERIFY:* Integration test validating adjusted payout band scoring.

- [ ] **Task 2.3: Same-Game Multi Grid Intersector (`SameGameMultiService.cs`)**
  - **Agent:** `backend-specialist` | **Skills:** `clean-code`, `testing-patterns`
  - Implement Dixon-Coles bivariate score tensor evaluator. Define standard market predicate masks (1X2, Over/Under, BTTS, Double Chance).
  - Compute exact joint probability $P = \sum_{(x, y) \in \mathcal{S}} P(x, y)$ and fair decimal odds $O = 1 / P$.
  - *INPUT:* Dixon-Coles parameters ($\lambda, \mu, \rho$), List of intra-match selections.
  - *OUTPUT:* `SameGameMultiResult` (ExactJointProbability, FairOdds, CorrelationCorrelationBonus, IsValid).
  - *VERIFY:* Unit tests in `SameGameMultiServiceTests.cs` verifying known combinations (e.g. Home Win + Over 2.5 vs independent product).

- [ ] **Task 2.4: Bet Builder Betslip Tier (`BetBuilderSlipComposer.cs`)**
  - **Agent:** `backend-specialist` | **Skills:** `clean-code`
  - Create dedicated Same-Game Multi slip generator for marquee fixtures of the day.
  - Package top 2-leg and 3-leg correlated combos where the market offers value relative to the exact Dixon-Coles joint probability.
  - *INPUT:* Daily marquee fixtures with Dixon-Coles models and live market odds.
  - *OUTPUT:* Generated Bet Builder betslip stored in `BetslipSet`.
  - *VERIFY:* Integration test generating Bet Builder slips for valid fixtures.

---

### Phase 3: Final Verification & Safety Protocol (Phase X)
- [ ] **Task 3.1: Full Solution Test Verification**
  - Execute `dotnet test MatchPredictor.Tests.Unit` (verify 320+ unit tests pass).
  - Execute `dotnet test MatchPredictor.Tests.Integration` (verify 440+ integration tests pass).
- [ ] **Task 3.2: Antigravity Master Checklist**
  - Run `python3 .agent/scripts/checklist.py .` and ensure Security, Lint, Schema, Tests, UX, and SEO pass cleanly with 0 errors.

---

## 5. Strategic Socratic Discovery & Trade-Offs

Before executing implementation, consider the following architectural choices:

### Decision Point 1: Hypothesis Test for Challenger Model Promotion
- **Option A (Recommended - Wilcoxon Signed-Rank Test):** Non-parametric test. Does not assume normality of loss differentials; highly robust to occasional outlier match results (e.g. 5-0 blowouts) that distort sample mean.
- **Option B (Paired t-test):** Parametric test. Standard in classical statistics but sensitive to heavy-tailed prediction losses.
*Recommendation:* Proceed with Wilcoxon Signed-Rank Test with fallback to asymptotic $Z$-test for $N \ge 25$.

### Decision Point 2: Cross-Match Acca Correlation Clustering Scope
- **Option A (Recommended - League & Kickoff Window Equicorrelation):** Assign empirical $\rho \approx 0.05 \text{ to } 0.12$ exclusively to legs sharing the same league and kickoff day, while treating cross-league legs as independent ($\rho = 0$). Computationally clean, low latency.
- **Option B (Full Covariance Matrix Decomposition):** Estimate full $M \times M$ covariance matrix via Cholesky decomposition.
*Recommendation:* Proceed with Option A. Football cross-match correlation is primarily intra-league (environment/rules/motivational clustering); full $M \times M$ empirical matrices suffer from estimation noise on small samples.

### Decision Point 3: Same-Game Multi (Bet Builder) Presentation
- **Option A (Recommended - Dedicated Bet Builder Slip Tier):** Generate a dedicated "AI Bet Builder" slip tier (holding 1–2 marquee matches with 2–3 intra-match legs each) alongside Banker, Ladder, and Draws slips.
- **Option B (Value Bets Only):** Present SGM combinations only on the individual match detail page and Value Bets tab without adding a new betslip tier.
*Recommendation:* Proceed with Option A. This complements the existing betslip set structure seamlessly.

---

## 6. Phase X: Verification & Definition of Done

```markdown
## ✅ PHASE X READINESS
- [ ] FeatureDriftMath & ModelPromotionGate unit tests passing
- [ ] SameGameMulti & GaussianCopulaAccumulator unit tests passing
- [ ] Database migrations/entities mapped and verified in Integration Tests
- [ ] Hangfire recurring jobs registered and passing job verification
- [ ] Python master audit checklist (security, lint, schema, test runner) returns PASSED
```
