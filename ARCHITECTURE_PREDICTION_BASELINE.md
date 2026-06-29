# Prediction Architecture & Baseline

This document describes the prediction model, its input features, the backtesting
methodology that gates changes, and the exact definitions of every KPI surfaced on the
analytics dashboard. It is the canonical reference for anyone changing model behaviour.

## 1. Prediction flow

```
ingest  ─►  candidate generation  ─►  correction  ─►  calibration  ─►  threshold filter  ─►  publish
(scrape/Excel)   (DataAnalyzerService)   (raw→corrected)   (bucket/beta)   (per-market)
```

- **`AnalyzerService`** orchestrates ingestion, candidate generation, publish selection,
  settlement, and the daily learning loops.
- **`DataAnalyzerService`** builds market candidates, blends model signals, then applies
  calibration and per-market threshold filtering.
- **`CalibrationService`** rebuilds bucket/beta calibration profiles from settled
  `ForecastObservation`s.
- **`ThresholdTuningService`** rebuilds publish thresholds from recent settled forecasts
  using train/validation splits and safe promotion checks.
- **`ForecastEvaluationService`** computes the top-line accuracy, calibration, and realized
  betting KPIs shown on `/Analytics` (see §6).

## 2. The model

The published probability for each market is an **ensemble** of up to four signals, pooled
in **logit space** (a weighted geometric mean of odds — the standard, well-behaved way to
combine calibrated probabilities). See `EnsembleProbabilityBlender`.

| Signal | Source | Default weight (`EnsembleWeights`) |
|---|---|---|
| Market | De-vigged bookmaker odds (`OddsMath`) | `1.0` |
| Base | Hand-weighted `ProbabilityCalculator` over scraped market lines | `0.6` |
| Dixon-Coles | `DixonColesModel` bivariate-Poisson goals model | `1.1` |
| Elo | `EloRatingModel` rating-based 1X2 | `0.7` |

After blending, the 1X2 triple is renormalized to sum to 1 and Under 2.5 is forced to be the
complement of Over 2.5 for internal consistency. Any null signal is skipped, so the ensemble
degrades gracefully when a model has insufficient data for a fixture.

### 2.1 Margin removal (de-vig) — `OddsMath`
Quoted decimal odds carry an overround (booksum − 1). Two de-vig methods are available:
- **Multiplicative / proportional:** scale each implied probability so the set sums to 1.
- **Power:** solve for exponent `k` with `Σ implied_iᵏ = 1` via bisection (better for
  favourite-longshot bias). When there is no margin, it falls back to proportional.

### 2.2 Dixon-Coles goals model — `DixonColesModel`
- Fits per-team attack/defence strengths plus a global home-advantage term by gradient
  ascent on the time-decayed Poisson log-likelihood.
- Historical matches are weighted with an **exponential time-decay** (configurable
  half-life in days) so recent form dominates.
- Builds a score matrix up to a max goals-per-side, applies the Dixon-Coles low-score
  dependency correction (τ), then derives every market (1X2, Over/Under, BTTS) from the
  matrix. Teams below a minimum finished-match count are treated as unknown.

### 2.3 Elo ratings — `EloRatingModel`
- Classic logistic Elo (scale 400) with a home-advantage bump and optional
  margin-of-victory scaling (FiveThirtyEight style). Ratings are trained by replaying
  results in chronological order. The win/draw/away split is derived from the rating gap
  with a configurable max draw share.

## 3. Self-learning (post-inference loops)

Learning happens **after** inference, which keeps the base models stable and auditable:
1. **Calibration** — bucket and beta (logistic) calibration profiles map raw probabilities
   to empirically observed frequencies per market. Beta is promoted only when it beats the
   bucket baseline on a held-out validation split.
2. **Threshold tuning** — per-market publish thresholds are retuned on recent settled
   forecasts with a train/validation split and promoted/demoted with hysteresis; every
   change is recorded in `PromotionHistory` (surfaced as the promotion timeline).
3. **Probability correction** — a trainable correction layer (`IProbabilityCorrectionService`)
   adjusts raw probabilities before calibration, with the same safe-promotion discipline.

Feature-contribution diagnostics for each forecast are persisted on `ForecastObservation`
(JSON) for explainability and surfaced on the analytics UI.

## 4. Backtesting methodology

All model changes are gated by **walk-forward (rolling-origin) backtesting**
(`WalkForwardBacktester`):
- Repeatedly **train on everything strictly before** a test window, then evaluate only the
  fixtures inside that window — so there is **no look-ahead leakage**.
- Aggregating the out-of-sample folds gives an honest estimate of live performance.
- `PointInTimeBacktestingSelector` enforces point-in-time data selection for the same
  reason during offline analysis.
- `BacktestEvaluator` turns the out-of-sample samples into the metric set in §6.

### 4.1 Regression guard
`BacktestRegressionGuardTests` (in `MatchPredictor.Tests.Unit`) runs the full
train → blend → evaluate path on a **seeded synthetic season** and fails the build if a
change degrades out-of-sample **Brier/ECE/ROI** past a locked tolerance, or drops below the
market baseline. This is the automated gate for "did this change actually help?".

## 5. Data model conventions

### 5.1 Canonical date/time
`Prediction`, `MatchData`, and `ForecastObservation` expose **canonical** temporal fields
that are authoritative for all logic:
- `MatchLocalDate` (`DateOnly`) — local match date.
- `MatchLocalTime` (`TimeOnly?`) — local kickoff time.
- `MatchDateTime` (`DateTime?`) — kickoff instant in UTC.

The legacy `string Date` / `string Time` properties are **deprecated**. They are still
written/backfilled for one release for backward compatibility but must not be used in new
logic; reads resolve canonical-first with the legacy strings only as a parse fallback for
pre-migration rows (see `FixtureIdentityFactory`, `PredictionQueries`). They are scheduled
for removal in a future destructive migration.

### 5.2 Razor page query abstractions
Razor pages do not depend on `ApplicationDbContext` directly. Read access is routed through
query-service abstractions so pages stay testable and persistence-agnostic:
- `IPredictionQueries` — published-prediction lists by market/date.
- `IAnalyticsQueries` — the `/Analytics` data snapshot (predictions, forecasts, odds
  snapshots, threshold/beta profiles, promotion history).
- `IHealthQueryService` — `/ops/health` signals, prediction coverage counts, source-quality
  profiles (including the missing-table fallback).
- `IScrapeStatusQueries` — recent scraping logs for `/ScrapeStatus`.

## 6. KPI definitions

All KPIs are computed by `ForecastEvaluationService` from settled `ForecastObservation`s and
`PredictionOddsSnapshot`s, and rendered on `/Analytics` for the Today / Yesterday / Last 3 /
Last 7 day windows.

### 6.1 Accuracy & calibration
| KPI | Definition |
|---|---|
| **Overall accuracy** | Correct settled predictions ÷ completed predictions. |
| **Brier score** | Mean squared error between predicted probability and outcome (0 = perfect, lower is better). Tracked raw vs calibrated. |
| **Log loss** | Mean negative log-likelihood of the realized outcomes (lower is better). |
| **Expected Calibration Error (ECE)** | Weighted average gap between predicted probability and observed frequency across probability bins (0 = perfectly calibrated). Tracked raw vs calibrated. |
| **Precision / Recall / F1** | Standard binary classification metrics for each published market (positive = the market was published as a bet). |
| **Reliability curve** | Per-bin observed frequency vs average predicted probability (the calibration plot). |

### 6.2 Realized betting performance (flat stake = 1 unit unless noted)
| KPI | Definition |
|---|---|
| **Settled / winning bet count** | Number of settled published bets and how many won. |
| **Win rate** | Winning bets ÷ settled bets. |
| **Total staked / net profit (units)** | Sum of stakes and the net P&L at the taken price. |
| **ROI %** | Net profit ÷ total staked × 100. |
| **Yield %** | Net profit per unit staked, expressed as a percentage (synonymous with ROI for flat stakes; reported for clarity vs Kelly). |
| **Max drawdown (units)** | Worst peak-to-trough decline of the cumulative flat-stake P&L curve. |
| **Average odds** | Mean decimal odds of settled bets. |
| **Kelly fraction / staked / net profit / ROI / bet count** | Same P&L recomputed with **fractional-Kelly** staking driven by the model edge, so staking quality is visible separately from selection quality. |

### 6.3 Closing Line Value (CLV)
| KPI | Definition |
|---|---|
| **Closing-line samples** | Bets that have both a publish and a closing odds snapshot. |
| **Average CLV %** | Mean of (taken odds ÷ closing odds − 1) over those bets — the standard long-run edge proxy. |
| **Beat-close rate** | Share of bets whose taken odds were better than the close. |

## 7. Status of historical bottlenecks

The bottlenecks identified in the original baseline have been addressed:
- ✅ Quality metrics per market/confidence bin (log-loss, precision/recall/F1, ECE) — §6.
- ✅ Realized bet-performance rollups (ROI/yield/win-rate/drawdown/Kelly) — §6.2.
- ✅ Aggregate CLV KPI from publish/close snapshots — §6.3.
- ✅ Feature-contribution diagnostics persisted per forecast — §3.
- ✅ Statistical base model (Dixon-Coles + Elo ensemble) replacing the purely hand-weighted
  calculator — §2.

Remaining direction: recency-aware weighting for the rebuild loops, and continued expansion
of the trainable correction layer under the walk-forward promotion gate.
