# ScrapeTest

`ScrapeTest` is a developer-only console tool for exercising scraper, scoring, benchmark, and historical dataset workflows against a real development database.

## Usage

Set one of these connection-string environment variables before running:

```shell
export ConnectionStrings__DefaultConnection="Host=...;Database=...;Username=...;Password=..."
```

or:

```shell
export MATCHPREDICTOR_DEV_DB="Host=...;Database=...;Username=...;Password=..."
```

Run from the solution directory:

```shell
dotnet run --project ScrapeTest/ScrapeTest.csproj -- --score
dotnet run --project ScrapeTest/ScrapeTest.csproj -- --score-backfill
dotnet run --project ScrapeTest/ScrapeTest.csproj -- --benchmark
dotnet run --project ScrapeTest/ScrapeTest.csproj -- --export-history 540 historical-dataset.json
dotnet run --project ScrapeTest/ScrapeTest.csproj -- --import-history historical-dataset.json --replace
```

Do not commit generated `bin/`, `obj/`, or exported dataset artifacts.
