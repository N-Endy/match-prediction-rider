# MatchPredictor

Football match prediction system: scrapes fixture and market data, computes market probabilities, applies a self-learning correction/calibration pipeline, publishes picks, settles them against real results, and retrains nightly.

## Solution layout

| Project | Purpose |
|---|---|
| `MatchPredictor.Domain` | Entities, domain models, service interfaces. No dependencies. |
| `MatchPredictor.Infrastructure` | EF Core (PostgreSQL) persistence, scrapers (Selenium/HTTP), the prediction math (`ProbabilityCalculator`), and the learning loop (`ProbabilityCorrectionService`, `CalibrationService`, `ThresholdTuningService`). |
| `MatchPredictor.Application` | Orchestration: `AnalyzerService` (sync, generation, settlement), `ValueBetsService`, AI chat/advisor services. |
| `MatchPredictor.Web` | Razor Pages UI, API controllers, Hangfire dashboard and recurring jobs. |
| `MatchPredictor.Tests.Integration` | xUnit tests (in-memory EF). `*InvestigateTests` drive a real browser and are excluded from CI. |
| `ScrapeTest` | Console harness for scraper/backtest experiments. |

## Prediction pipeline

```
scraped market odds
   └─ ProbabilityCalculator      → raw probability
       └─ ProbabilityCorrectionService (logit meta-model, per market) → corrected probability
           └─ CalibrationService (bucket or beta, per market)         → calibrated probability
               └─ ThresholdTuningService (publish threshold, per market)
                   └─ published prediction + ForecastObservation
```

All three learning services retrain from settled `ForecastObservations` in the nightly `daily-analysis-job`:

- **Correction** trains on `RawProbability` (its inference input).
- **Calibration** trains on `CorrectedProbability` (its inference input).
- Candidate models are promoted only when they beat the incumbent on held-out weighted Brier score.
- Training uses point-in-time snapshots (`PointInTimeBacktestingSelector`) so regenerated revisions and post-kickoff rows cannot leak into the history.

## Running locally

Prerequisites: .NET 10 SDK, PostgreSQL (or use docker-compose).

```bash
# database + app
docker compose up --build

# or run against your own Postgres
export ConnectionStrings__DefaultConnection="Host=localhost;Database=matchpredictor;Username=...;Password=..."
dotnet run --project MatchPredictor.Web
```

Migrations apply automatically at startup. Secrets (Groq, ApiFootball, Hangfire credentials) are supplied via environment variables — see the comments in `docker-compose.yml`. Never commit secrets.

### Runtime toggles

| Variable | Effect |
|---|---|
| `RUN_BACKGROUND_JOBS` | Enables the Hangfire server and recurring jobs (defaults to **true** when unset). |
| `ENABLE_BROWSER_SCRAPING` | Enables Selenium/Chrome scraping jobs. |
| `ENABLE_USER_TRACKING` | Enables the visitor tracking middleware. |
| `USE_EXTERNAL_CRON` | When **true** on the worker, disables Hangfire recurring registration and expects cron-job.org HTTP triggers instead. |
| `CronJob__Secret` | Shared secret for `X-Cron-Secret` header on `POST /api/ops/jobs/{jobName}` (worker only). |

### External cron (cron-job.org hybrid)

By default the worker registers recurring jobs inside Hangfire. If Render’s in-process scheduler is unreliable, switch to **hybrid mode**: cron-job.org fires HTTP triggers; Hangfire still executes the queue.

**Worker env (hybrid mode):**

```
RUN_BACKGROUND_JOBS=true
ENABLE_BROWSER_SCRAPING=true
USE_EXTERNAL_CRON=true
CronJob__Secret=<long-random-secret>
```

**Rollout:**

1. Deploy with `USE_EXTERNAL_CRON=false` (no behavior change; trigger API is available).
2. Set `CronJob__Secret` on the worker and smoke-test:

```bash
curl -X POST "https://<worker-host>/api/ops/jobs/score-update" \
  -H "X-Cron-Secret: <secret>"
```

3. Create cron-job.org jobs pointing at the **worker** URL (not web). Set account timezone to **Africa/Lagos** (WAT).
4. Set `USE_EXTERNAL_CRON=true` and redeploy the worker (clears Hangfire recurring rows).
5. Monitor `/ops/health` and `/hangfire`.

Each cron job: `POST`, header `X-Cron-Secret: <secret>`, expect HTTP **202**.

| cron-job.org path suffix | Schedule (WAT) |
|---|---|
| `prediction-prewarm` | `40 23 * * *` |
| `daily-analysis` | `20 0 * * *` |
| `prediction-generation` | `35 0 * * *` |
| `prediction-generation-post-analysis` | `30 4 * * *` |
| `prediction-generation-refresh` | `30 12,16 * * *` |
| `cleanup-old-predictions` | `0 1 * * *` |
| `score-update` | `*/6 * * * *` |
| `score-backfill` | `17 * * * *` |
| `closing-line-snapshot` | `*/5 * * * *` |

Optional recovery: `POST /api/ops/jobs/startup-catchup` enqueues daily analysis followed by data sync (same as worker startup catch-up).

**Note:** 5-minute and 6-minute jobs generate ~460 HTTP calls/day. Confirm your cron-job.org plan supports that frequency. Do not enable `USE_EXTERNAL_CRON=true` until cron-job.org jobs exist, or scheduling will gap. Enabling both internal Hangfire recurring and cron-job.org causes duplicate runs.

## Tests

```bash
dotnet test MatchPredictor.Tests.Integration/MatchPredictor.Tests.Integration.csproj \
  --filter "FullyQualifiedName!~Investigate"
```

`PredictionPipelineGuardTests` are the core regression guards: they verify the raw → corrected → calibrated ordering with the real math services, the three learning-loop rebuilds, and an end-to-end `GeneratePredictionsAsync` run.

CI runs the same suite on every push/PR (`.github/workflows/ci.yml`).

## Operational surfaces

| Path | Auth |
|---|---|
| `/Analytics` | Basic auth (`UsageDashboard:*`, falls back to `Hangfire:*`) |
| `/hangfire` | Basic auth (`Hangfire:Username`/`Password`) |
| `/admin/usage`, `/ScrapeStatus`, `/ops/health` | Basic auth (`UsageDashboard:*`, falls back to `Hangfire:*`) |
| `POST /api/ops/jobs/{jobName}` | `X-Cron-Secret` header (`CronJob:Secret`) — worker only |
| `/health` | Public liveness check |

Further design notes: `ARCHITECTURE_NOTES.md` and `ARCHITECTURE_PREDICTION_BASELINE.md`.
