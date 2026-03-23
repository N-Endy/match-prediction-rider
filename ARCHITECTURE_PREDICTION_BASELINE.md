# Prediction Baseline and Bottlenecks

## Current flow
- `AnalyzerService` orchestrates data ingestion, candidate generation, publish selection, settlement, and daily learning loops.
- `DataAnalyzerService` creates market candidates from `ProbabilityCalculator`, then calibrates and threshold-filters them.
- `CalibrationService` rebuilds bucket/beta calibration profiles from settled `ForecastObservations`.
- `ThresholdTuningService` rebuilds publish thresholds from recent settled forecasts using train/validation splits.
- `ForecastEvaluationService` computes top-line accuracy and Brier-oriented calibration diagnostics.

## What self-learning currently means
- The base probability model is deterministic and hand-weighted (`ProbabilityCalculator`).
- Learning happens after inference via:
  - probability calibration profile updates, and
  - threshold promotion/demotion.
- This setup can improve reliability but has limited capacity to learn nonlinear drift in the base model itself.

## Main bottlenecks
- Missing quality metrics for some decision contexts (log-loss, precision/recall/F1 by market and confidence bins).
- No realized bet performance rollups (ROI/yield/win rate/drawdown) at report level.
- No aggregate CLV KPI despite publish/close odds snapshots being captured.
- Rebuild logic uses fixed windows and equal weighting, which may react slowly to regime shifts.
- Limited observability of feature-level contribution diagnostics per forecast.

## Improvement direction
- Expand metrics and report-level KPIs first.
- Add recency-aware weighting for calibration/threshold rebuild loops.
- Persist feature-contribution summaries for diagnostics and analysis.
- Add a trainable correction layer on top of base probabilities with safe promotion checks.
