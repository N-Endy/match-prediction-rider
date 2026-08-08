# MatchPredictor

Football match prediction system: scrapes fixture and market data, computes market probabilities, applies a self-learning correction/calibration pipeline, publishes picks, settles them against real results, and retrains nightly.

## Solution layout

| Project | Purpose |
|---|---|
| `MatchPredictor.Domain` | Entities, domain models, service interfaces. No dependencies. |
| `MatchPredictor.Infrastructure` | EF Core (PostgreSQL) persistence, scrapers (Selenium/HTTP), the prediction math (`ProbabilityCalculator`), and the learning loop (`ProbabilityCorrectionService`, `CalibrationService`, `ThresholdTuningService`). |
| `MatchPredictor.Application` | Orchestration: `AnalyzerService` (sync, generation, settlement), `ValueBetsService`, `BetslipGenerationService`, AI chat/advisor services. |
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

Migrations apply automatically at startup. Secrets (OpenAI/`AiLlm__ApiKey`, Gemini/`AiLlm__Fallback__ApiKey` or `GEMINI_API_KEY`, ApiFootball, Hangfire credentials) are supplied via environment variables — see the comments in `docker-compose.yml`. Legacy `GroqApiKey` still works. Never commit secrets.

AI chat / advisor defaults to **GPT-5.6 Luna** (`AiLlm:Provider=openai`) with automatic failover to **Gemini 3.5 Flash**. Set an OpenAI Platform key (`AiLlm__ApiKey`) and a Gemini key (`AiLlm__Fallback__ApiKey` or `GEMINI_API_KEY`). Either key alone still works (OpenAI-only or Gemini-only via `AiLlm__Provider=gemini`). For Groq, set `AiLlm__Provider=groq` and `GroqApiKey`.

### Runtime toggles

| Variable | Effect |
|---|---|
| `RUN_BACKGROUND_JOBS` | Enables the Hangfire server and recurring jobs (defaults to **true** when unset). |
| `ENABLE_BROWSER_SCRAPING` | Enables Selenium/Chrome scraping jobs. |
| `ENABLE_USER_TRACKING` | Enables the visitor tracking middleware. |
| `USE_EXTERNAL_CRON` | When **true** on the worker, disables Hangfire recurring registration and expects cron-job.org HTTP triggers instead. |
| `CronJob__Secret` | Shared secret for `X-Cron-Secret` header on `POST /api/ops/jobs/{jobName}` (worker only). |
| `Hangfire__WorkerCount` | Hangfire parallel workers (defaults to **1** on Render/Railway; override only if you have enough RAM). |

### Worker deployment (Render)

The worker runs Chrome-based scraping and needs more memory than the web service.

| Requirement | Recommendation |
|---|---|
| **Instance RAM** | **2GB minimum** (Render Standard). 512MB Starter will OOM when Chrome + .NET run together. |
| `RUN_BACKGROUND_JOBS` | `true` |
| `ENABLE_BROWSER_SCRAPING` | `true` |
| `Hangfire__WorkerCount` | `1` on constrained hosts (also the default on Render) |

All analyzer Hangfire jobs share a single execution lock (`matchpredictor-analyzer`) so daily analysis, score updates, and scraping never run in parallel — this prevents memory spikes when cron-job.org triggers overlap.

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
| `score-update` | `*/12 * * * *` |
| `score-backfill` | `17 * * * *` |
| `closing-line-snapshot` | `*/5 * * * *` |
| `betslip-generation` | `0 5,13 * * *` |

Optional recovery: `POST /api/ops/jobs/startup-catchup` enqueues daily analysis followed by data sync (same as worker startup catch-up).

**Note:** The closing-line snapshot job runs every 5 minutes. Do not enable `USE_EXTERNAL_CRON=true` until cron-job.org jobs exist, or scheduling will gap. Enabling both internal Hangfire recurring and cron-job.org causes duplicate runs.

## Tests

The suite is split into two projects:

- **`MatchPredictor.Tests.Unit`** — fast, deterministic unit tests for the math/ML core
  (Dixon-Coles, Elo, de-vig odds math, the logit ensemble blender, and the backtest
  evaluator/walk-forward harness) plus the source-resilience primitives. It also contains
  the **backtest regression guards** (`BacktestRegressionGuardTests`): on a seeded synthetic
  season they fail the build if a model change degrades out-of-sample Brier/ECE/ROI past a
  locked tolerance or drops below the market baseline. Coverage of the statistical core is
  enforced at ≥80% line via coverlet (configured in the project file).
- **`MatchPredictor.Tests.Integration`** — exercises the prediction pipeline end to end
  against an in-memory store: `DataAnalyzerService` candidate generation, the raw → corrected
  → calibrated ordering, the calibration/threshold/meta-model learning loops
  (`CalibrationServiceTests`, `ThresholdTuningServiceTests`), report KPIs
  (`ForecastEvaluationServiceTests`, `BettingPerformanceStatsTests`, `CalibrationKpiTests`),
  and an end-to-end `GeneratePredictionsAsync` run (`AnalyzerServiceBackfillTests`).

```bash
# Unit suite + coverage gate
dotnet test MatchPredictor.Tests.Unit/MatchPredictor.Tests.Unit.csproj /p:CollectCoverage=true

# Integration suite (excludes live-browser/live-network and the nightly Postgres E2E)
dotnet test MatchPredictor.Tests.Integration/MatchPredictor.Tests.Integration.csproj \
  --filter "FullyQualifiedName!~Investigate&Category!=LiveNetwork&Category!=PostgresE2E"
```

`PostgresE2E`-tagged tests run real EF Core migrations against PostgreSQL; they no-op unless a
connection string is supplied via `MATCHPREDICTOR_TEST_POSTGRES` (or
`ConnectionStrings__DefaultConnection`).

CI runs the unit suite (with the coverage gate) and the integration suite on every push/PR,
and runs the Postgres E2E suite against a Postgres service container on a nightly schedule
(`.github/workflows/ci.yml`).

## Operational surfaces

| Path | Auth |
|---|---|
| `/Analytics` | Basic auth (`UsageDashboard:*`, falls back to `Hangfire:*`) |
| `/hangfire` | Basic auth (`Hangfire:Username`/`Password`) |
| `/admin/usage`, `/ScrapeStatus`, `/ops/health` | Basic auth (`UsageDashboard:*`, falls back to `Hangfire:*`) |
| `POST /api/ops/jobs/{jobName}` | `X-Cron-Secret` header (`CronJob:Secret`) — worker only |
| `/health` | Public liveness check |

## Reverse proxy / forwarded headers

When deployed behind Render, Railway, or another reverse proxy, the app enables
`ForwardedHeaders` (`X-Forwarded-For`, `X-Forwarded-Proto`) in [`Program.cs`](MatchPredictor.Web/Program.cs).
`KnownProxies` and `KnownIPNetworks` are cleared so the platform load balancer is trusted —
this is required on PaaS hosts where the proxy IP is not fixed. Rate limiting and visitor
tracking use the first `X-Forwarded-For` hop as the client key when present.

If you self-host behind a single known reverse proxy, consider pinning that proxy's IP in
`ForwardedHeadersOptions.KnownProxies` instead of clearing the list.

Further design notes: `ARCHITECTURE_NOTES.md` and `ARCHITECTURE_PREDICTION_BASELINE.md`.
