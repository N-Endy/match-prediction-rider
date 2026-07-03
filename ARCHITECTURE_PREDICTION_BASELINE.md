# Prediction Architecture & Baseline

This document describes the prediction model, its input features, the backtesting
methodology that gates changes, and the exact definitions of every KPI surfaced on the
analytics dashboard. It is the canonical reference for anyone changing model behaviour.

## 1. Prediction flow

```
ingest (sports-ai.dev Excel + SportyBet pricing)
   └─ candidate generation (DataAnalyzerService)
       └─ correction (ProbabilityCorrectionService)
          └─ calibration (CalibrationService — bucket/beta/isotonic)
               └─ threshold filter (ThresholdTuningService)
                   └─ publish + ForecastObservation
```

- **`AnalyzerService`** orchestrates ingestion, candidate generation, publish selection,
  settlement, and the daily learning loops.
- **`DataAnalyzerService`** builds market candidates, blends model signals, then applies
  calibration and per-market threshold filtering.
- **`LearningLoopService`** rebuild order: ensemble stacking weights → ML.NET LightGBM →
  correction → calibration → thresholds.
- **`ForecastEvaluationService`** computes accuracy, calibration, and betting KPIs on
  `/Analytics` (basic-auth protected).

## 2. The model

The published probability for each market is an **ensemble** of up to six signals, pooled
in **logit space** (weighted geometric mean). See `EnsembleProbabilityBlender` and
`EnsembleWeights`.

| Signal | Source | Role | Default weight (`EnsembleWeights.ProductionDefault`) |
|---|---|---|---|
| **Bookmaker** | De-vigged SportyBet decimal odds (`SourceMarketDeVig` + `OddsMath`) | True market anchor | `1.2` |
| **Market** | sports-ai.dev Excel feed as shaped by `ProbabilityCalculator` | Third-party model signal (not bookmaker odds) | `1.0` |
| **Base** | Reserved hand-weighted path | Unused in production | `0.0` |
| **DixonColes** | `DixonColesModel` + internal Elo pre-blend via `StatisticalSignalProvider` | Statistical goals/ratings core | `1.1` |
| **Elo** | Standalone Elo in outer blend | Unused in production (Elo is inside statistical core) | `0.0` |
| **Ml** | Promoted per-market ML.NET LightGBM model (`MarketMlModelProfiles`) | Optional learned replacement/augmenting signal | `0.8` |

Per-market weights are learned nightly by `EnsembleWeightTuningService` (logit stacking grid
search, walk-forward holdout, promotion only when holdout Brier improves ≥ 0.0015 vs the
incumbent). Learned profiles persist in `EnsembleWeightProfiles`.

After blending, the 1X2 triple is renormalized to sum to 1 and Under 2.5 is the complement
of Over 2.5. Null signals are skipped so the ensemble degrades gracefully.

**Published markets:** BTTS, Over/Under 2.5, Home Win, Away Win, Draw. BTTS is published
only when an explicit BTTS market quote exists (SportyBet enrichment or stored pair) —
Poisson-derived BTTS alone is not published.

### 2.1 Data inputs

| Input | Source | Notes |
|---|---|---|
| sports-ai.dev Excel | `ExtractFromExcel` / `WebScraperService` | Pre-normalized probabilities; validated by `ExcelFeedQualityValidator` |
| SportyBet pricing | `SportyBetBookingService` | Full 1X2, O/U 2.5, BTTS; persisted in `MarketOddsSnapshots` |
| Historical scores | FlashScore / AiScore / SofaScore / API-Football | Dixon-Coles + Elo training |

When the Excel feed returns zero rows, the pipeline **degrades** to bookmaker-sourced
fixtures (`SourceFixtureMatchDataFactory`) when SportyBet quotes that date.

### 2.2 Margin removal (de-vig) — `OddsMath` / `SourceMarketDeVig`

Production bookmaker de-vig (via `SourceMarketDeVig`):
- **Shin (1992)** for 1X2 (three-outcome markets).
- **Power** for two-way markets (Over/Under 2.5, BTTS).
- **Proportional fallback** when only source probabilities are available (no margin to remove).

The sports-ai.dev feed is already probability-normalized on ingest
(`NormalizeSourceProbabilities`) — Shin/Power are **not** applied to that feed.

### 2.3 Dixon-Coles goals model — `DixonColesModel`

- Per-team attack/defence + home advantage via adaptive-gradient optimization on
  time-decayed Poisson LL.
- Exponential time-decay (default 90-day half-life), with walk-forward half-life tuning.
- League-specific models are fitted when enough history exists, with shrinkage toward a
  global fallback model for sparse teams/leagues.
- Dixon-Coles τ low-score correction uses a refined bounded rho search; derives 1X2,
  O/U, BTTS from score matrix.
- Teams below minimum finished-match count are unknown (signal skipped).

### 2.4 Elo ratings — `EloRatingModel`

Classic logistic Elo with home-advantage bump; blended with Dixon-Coles inside
`StatisticalSignalProvider` before entering the outer ensemble as the DixonColes signal.

## 3. Self-learning (post-inference loops)

All rebuild loops use **exponential recency weighting** (`RecencyWeighting`):

1. **Ensemble stacking** (`EnsembleWeightTuningService`, 30-day half-life) — per-market
   logit weight grid; holdout Brier promotion gate.
2. **ML.NET LightGBM** (`MarketPredictionModelService`) — per-market binary classifiers
   promoted only when holdout Brier improves vs the incumbent ensemble.
3. **Probability correction** (`ProbabilityCorrectionService`, 30-day half-life) — logit
   meta-model on raw probabilities.
4. **Calibration** (`CalibrationService`, 30-day half-life) — bucket, beta, and isotonic calibrators.
5. **Threshold tuning** (`ThresholdTuningService`, 21-day half-life) — publish thresholds
   for BTTS, O/U 2.5, 1X2 sides, and Draw.

Feature-contribution JSON on each `ForecastObservation` records per-signal inputs
(bookmaker, calculator/feed, statistical) for explainability and weight learning.

## 4. Backtesting methodology

Walk-forward backtesting (`WalkForwardBacktester`) with `PointInTimeBacktestingSelector`.
`BacktestRegressionGuardTests` gates synthetic-season regressions on Brier/ECE/ROI.

## 5. Data model conventions

### 5.1 Canonical date/time

Authoritative fields: `MatchLocalDate`, `MatchLocalTime`, `MatchDateTime` (UTC).
Legacy string `Date`/`Time` are deprecated.

### 5.2 Key tables

| Table | Purpose |
|---|---|
| `MarketOddsSnapshots` | Point-in-time de-vigged bookmaker quotes per fixture |
| `EnsembleWeightProfiles` | Promoted per-market stacking weights |
| `ForecastObservations` | Settled forecast audit trail + feature JSON |
| `FixtureFeatureSnapshots` | Point-in-time form, rest, H2H, and API-Football feature captures |
| `MarketMlModelProfiles` | Promoted per-market LightGBM models and promotion metrics |
| `HistoricalBacktestSummaries` | Nightly real-history rolling Brier/ECE/ROI/CLV summaries |
| `PredictionOddsSnapshots` | Publish/close odds for CLV |

## 6. KPI definitions

See `ForecastEvaluationService` — accuracy, Brier, ECE, log-loss, flat-stake and Kelly
ROI, max drawdown, CLV (taken vs closing bookmaker odds).

## 7. Status

Addressed:
- Real bookmaker odds signal (SportyBet + de-vig + snapshots)
- Distinct sports-ai.dev feed signal (not mislabeled as "market")
- Learned ensemble stacking weights with promotion gate
- Excel feed quality monitoring + bookmaker degradation path
- BTTS publication gated on explicit market quotes
- Draw market in candidate generation and threshold tuning
- `/Analytics` behind basic auth

Remaining direction:
- `ProbabilityCalculator` hand-tuned constants are now configurable via
  `ProbabilityCalculatorSettings`; retirement should wait until ML + bookmaker signals
  pass real-history backtest gates for at least two weeks.
