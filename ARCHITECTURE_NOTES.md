## Date/time normalization plan

- **Current state**
  - `MatchData.Date` and `MatchData.Time` are stored as strings.
  - `Prediction.Date` and `Prediction.Time` are also strings, while `Prediction.CreatedAt` is a proper `DateTime`.
  - Many queries filter on the string `Date` using formatted values like `dd-MM-yyyy`.
  - Cleanup previously parsed these string dates in memory and removed entities in two passes.

- **Target state**
  - Use `DateTime` (or `DateTimeOffset` if you need explicit offsets) for match and prediction timestamps in the database.
  - Keep string representations only for display, derived from the canonical `DateTime` fields.

- **Suggested migration path**
  1. Add new `DateTime` properties (e.g., `MatchDateTime` on `MatchData` and `Prediction`) and map them to new columns in a future migration.
  2. Backfill these new columns from the existing string `Date`/`Time` using a data‑migration script or an ad‑hoc job.
  3. Update queries (Razor Pages and services) to filter on the new `DateTime` fields.
  4. Once everything reads/writes the new fields, optionally drop or de‑emphasize the original string properties.

- **What is implemented now**
  - Cleanup logic for old predictions now uses `Prediction.CreatedAt` (a real `DateTime`) and pushes the filter into SQL via `ExecuteDeleteAsync`.
  - Cleanup for `MatchData` still parses string dates, but does so in a single, optimized pass.

## Web-layer decoupling (query services)

- **Current state**
  - Razor Page models such as `BTTS` and `Over2` inject `ApplicationDbContext` directly and compose EF queries inline.

- **Target pattern**
  - Introduce read‑only query services in the application layer, for example:
    - `IPredictionQueries` with methods like `Task<IReadOnlyList<Prediction>> GetBTTSAsync(DateTime date)` and `GetOver25Async(DateTime date)`.
  - Inject these interfaces into page models, so the web layer no longer depends directly on `ApplicationDbContext`.

- **Suggested implementation steps**
  1. Create an application‑layer interface `IPredictionQueries` and an implementation that uses `ApplicationDbContext` internally.
  2. Refactor one or two key pages (e.g., `BTTS` and `Over2`) to consume `IPredictionQueries` instead of the DbContext.
  3. Gradually migrate the remaining pages to use the same query service.

## Overall improvements summary

- Neon/PostgreSQL is now the single source of truth via `ConnectionStrings:DefaultConnection`, sourced from environment variables for all environments.
- EF Core is configured with `UseNpgsql` and has a migration/snapshot pair that matches the current domain model, ready to be applied to Neon by running the app (which calls `db.Database.Migrate()` on startup).
- SQLite‑specific providers and Hangfire storage packages have been removed, along with legacy copied migrations, to reduce confusion and dependency surface.
- Cleanup logic has been fixed to:
  - Use `CreatedAt` for predictions and delete old data directly in SQL.
  - Correctly remove old `MatchData` rows without duplicating deletion calls.

