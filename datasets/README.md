# Versioned historical datasets

These files are deterministic, schema-versioned snapshots of finished `MatchScores`
(league, teams, score, kickoff, BTTS label, live flag) used to make backtests reproducible.

The on-disk format is produced and consumed by
`MatchPredictor.Domain.Sourcing.HistoricalDatasetSerializer`, which sorts rows by
`(matchTimeUtc, league, homeTeam, awayTeam, score)` so the same set of matches always
serializes to byte-identical JSON regardless of database row order — making the files safe
to commit and diff.

## Files

- `sample-historical-dataset.v1.json` — a small, committed seed dataset so the format is
  versioned and a deterministic backtest fixture exists in-repo without a database.

## Regenerating a real dataset from the production store

Export the last N days of finished results from the configured database:

```bash
# from the MatchPredictor solution root, with ConnectionStrings__DefaultConnection set
dotnet run --project ScrapeTest -- --export-history 540 datasets/historical-dataset.json
```

Import a committed dataset back into a store (e.g. a fresh dev database):

```bash
# add --replace to clear the covered time window before inserting
dotnet run --project ScrapeTest -- --import-history datasets/historical-dataset.json
```

`--import-history` skips fixtures that already exist (matched on kickoff + league + teams)
unless `--replace` is supplied.
