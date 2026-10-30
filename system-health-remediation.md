# Implementation Plan: System Health & Scraper Remediation

**Slug:** `system-health-remediation`  
**Status:** Completed  

## Overview
Remediated code safety warnings, scraper null-dereference hazards, SofaScore sitemap discovery inefficiency, and obsolete Hangfire bootstrapper calls identified during the comprehensive system audit.

---

## Tasks

- [x] **Task 1: Fix Unboxed Nullable Cast in Shadow Logging**
  - **File:** `MatchPredictor.Application/Services/AnalyzerService.PredictionGeneration.cs`
  - **Change:** Safely unpack `prediction.ConfidenceScore` using `(double)(prediction.ConfidenceScore ?? (decimal)candidate.CalibratedProbability)` with safe fallback.
  - **Verification:** Compiler warning CS8629 resolved; solution builds cleanly with 0 warnings.

- [x] **Task 2: Harden WebScraperService Null Guards**
  - **File:** `MatchPredictor.Infrastructure/WebScraperService.cs`
  - **Changes:**
    - Pass `rawTime ?? string.Empty` into `ExtractClockTime(rawTime)` in `ParseScoreMatchTime`.
    - Coalesce `container.GetAttribute("innerHTML") ?? string.Empty` in `ParseScoreDataHtml` invocation.
    - Guard `HtmlEntity.DeEntitize(next.InnerText ?? string.Empty)` against null node text.
  - **Verification:** Compiler warnings CS8602 and CS8604 eliminated in `WebScraperService.cs`.

- [x] **Task 3: Modernize SofaScore Sitemap Discovery & Early Exit**
  - **File:** `MatchPredictor.Infrastructure/WebScraperService.cs`
  - **Changes:**
    - Expanded event URL discovery filter to recognize modern SofaScore paths (`/football/`, `/event/`, `/match/`).
    - Added early-exit throttle to abort sitemap traversal after 5 consecutive sitemaps with zero match URLs (`maxConsecutiveEmptySitemaps = 5`).
  - **Verification:** Unit tests and integration tests pass; eliminates wasteful traversal of empty sitemaps per cycle.

- [x] **Task 4: Upgrade ScrapeTest Hangfire PostgreSQL Bootstrapper**
  - **File:** `ScrapeTest/Program.cs`
  - **Change:** Replaced obsolete `UsePostgreSqlStorage(string, ...)` with `c => c.UseNpgsqlConnection(connectionString)` matching `MatchPredictor.Web`.
  - **Verification:** Obsolete warning CS0618 eliminated.

- [x] **Task 5: Full Regression Testing & Verification**
  - **Commands:** Run `dotnet test` on both Unit (407) and Integration (459) suites, then execute `.agent/scripts/checklist.py .`.
  - **Verification:** All 866 tests pass; master checklist passes with 6/6 checks green.
