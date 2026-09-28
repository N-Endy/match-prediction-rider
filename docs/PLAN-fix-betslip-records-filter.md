# Plan: Fix Betslip Records Tab Switching Stale Date Shift

- **Slug:** `fix-betslip-records-filter`
- **Primary Agent:** `project-planner`
- **Collaborating Agents:** `frontend-specialist`, `backend-specialist`, `test-engineer`
- **Target Files:**
  - `MatchPredictor.Web/wwwroot/service-worker.js`
  - `MatchPredictor.Web/Pages/Betslips.cshtml`
  - `MatchPredictor.Web/Pages/Betslips.cshtml.cs`
  - `MatchPredictor.Tests.Integration/WebApplicationFactorySmokeTests.cs`
  - `MatchPredictor.Tests.Integration/BetslipsRecordsDateTests.cs`
  - `docs/PLAN-fix-betslip-records-filter.md`
- **Status:** COMPLETED & VERIFIED

---

## 1. Root Cause Analysis

### The Empirical Findings & Mystery Explained
When navigating to `/betslips`:
1. **Initial Page Load / Refresh Works (Shows Latest Banker Record):**
   - Initial loads or browser refreshes issue a document navigation request (`request.mode === 'navigate'`).
   - In `MatchPredictor.Web/wwwroot/service-worker.js` (lines 62–68):
     ```javascript
     if (request.mode === 'navigate') {
       event.respondWith(
         fetch(request)
           .catch(() => caches.match('/offline.html'))
       );
       return;
     }
     ```
   - Navigation requests deliberately bypass the service worker's `CacheStorage` and fetch fresh HTML directly from the server.
   - On the server, `BetslipsModel.OnGetAsync` defaults to `BetslipRecordSection.Banker`, queries `_betslipQueries.GetLatestSlipDateAsync(Banker)` (`2026-09-27`), and renders the latest record and calendar for September 2026.

2. **Why Tab Switching (e.g. Rollover, Ladder, AI Draws) Shifts the Date Way Back in Time:**
   - In `MatchPredictor.Web/Pages/Betslips.cshtml`, clicking a tab button executes client-side AJAX `fetch()` calls:
     - `fetchLatestDate(nextSection)`: `fetch('/betslips?handler=LatestRecordDate&record=' + nextSection)`
     - `fetchHtml(...)`: `fetch('/betslips?handler=RecordCalendar...')` and `fetch('/betslips?handler=RecordDay...')`
   - In `service-worker.js` (lines 70–86):
     ```javascript
     event.respondWith(
       caches.match(request).then((cached) => {
         if (cached) {
           return cached;
         }

         return fetch(request).then((response) => {
           if (!response || response.status !== 200 || response.type === 'opaque') {
             return response;
           }

           const copy = response.clone();
           caches.open(CACHE_NAME).then((cache) => cache.put(request, copy));
           return response;
         });
       })
     );
     ```
   - **Critical Vulnerability in Service Worker Caching Strategy:**
     The service worker operates a **cache-first** policy on *all* same-origin `GET` requests that are not navigations and don't begin with `/api/`, `/admin/`, or `/hangfire`.
     - When any tab is clicked, the AJAX request to `/betslips?handler=LatestRecordDate&record=rollover` was cached in the user's browser `CacheStorage` under `matchpredictor-v2` when the user first visited or tested that feature (in early September / August 2026).
     - Because `service-worker.js` returns `cached` first without revalidating with the server, it serves the ancient cached JSON response (`{ "date": "2026-08-01" }` or similar ancient date).
     - The client code receives that ancient date as `latest`, shifts the calendar to that old month (e.g. August 2026), and loads slips from that date.

3. **Why Recent Records No Longer Appear for Any Tab Until Page Refresh:**
   - Once an ancient date is returned:
     - `datesBySection[nextSection]` is overwritten with the old date.
     - `month` is set to the old month (`date.slice(0, 7)`).
     - Even if the user clicks back to Banker, `fetch('/betslips?handler=LatestRecordDate&record=banker')` is also intercepted by the service worker and returns its old cached response.
     - The calendar grid remains locked in August 2026. Since September dates do not exist in August's 42-day calendar matrix, no recent slips can ever be clicked or seen.
     - Refreshing the page is the only escape hatch because refresh triggers `request.mode === 'navigate'`, which bypasses `CacheStorage` and retrieves fresh HTML for Banker from the server.

4. **Flaws in Client-Side Date Selection Logic (`Betslips.cshtml`):**
   - Lines 243–245:
     ```javascript
     const remembered = datesBySection[nextSection] || null;
     const latest = await fetchLatestDate(nextSection);
     const date = remembered && latest
         ? (remembered > latest ? remembered : latest)
         : (latest || remembered);
     ```
   - The expression `remembered > latest ? remembered : latest` is fundamentally inverted and faulty: if `remembered` is later than `latest`, it selects a date that has no slips in that section; if `remembered` is a past date, it is ignored. When switching sections, the user expects to see the section's latest record (`latest || remembered`).
   - In `fetchLatestDate` and `fetchHtml`, requests lack `cache: 'no-store'` and cache-busting timestamp query parameters (`_t`), leaving them susceptible to both browser HTTP cache and service worker cache.

5. **Server-Side Cache-Control Header Gaps (`Betslips.cshtml.cs`):**
   - Handlers `OnGetLatestRecordDateAsync`, `OnGetRecordCalendarAsync`, and `OnGetRecordDayAsync` do not emit explicit `Cache-Control: no-cache, no-store, must-revalidate` headers.

---

## 2. Proposed Solution

### A. Restrict Service Worker to Static Assets & Purge Stale Caches
1. **Exclude Dynamic Data & Handlers in `service-worker.js`:**
   - Restrict runtime `CacheStorage` caching exclusively to true static assets (scripts, stylesheets, images, fonts, icons, manifest, and offline fallback).
   - Dynamic page handlers (requests with query parameters like `handler=` or paths like `/betslips`, `/predictions`) must always bypass `CacheStorage` and go directly to the network.
2. **Bump `CACHE_NAME` to `'matchpredictor-v3'`:**
   - The existing `activate` event in `service-worker.js` deletes all caches where `key !== CACHE_NAME`:
     ```javascript
     keys.filter((key) => key !== CACHE_NAME).map((key) => caches.delete(key))
     ```
   - Bumping the version immediately purges the stale `matchpredictor-v2` cache from all user devices upon activation.

### B. Harden Client Fetching & Tab Switching in `Betslips.cshtml`
1. **Always Fetch Fresh Section Dates (`cache: 'no-store'` + Cache-Buster):**
   - In `fetchLatestDate`: pass `cache: 'no-store'` and attach `&_t=${Date.now()}`.
   - In `fetchHtml`: pass `cache: 'no-store'` and attach `_t=${Date.now()}`.
2. **Fix Tab Switching Date Selection:**
   - When switching tabs, choose `latest || remembered`. This ensures clicking any tab (Rollover, Banker, Ladder, AI Draws) always navigates to that section's latest available slip date.

### C. Server-Side No-Cache Headers in `Betslips.cshtml.cs`
- Add `Response.Headers.CacheControl = "no-cache, no-store, must-revalidate"` to:
  - `OnGetLatestRecordDateAsync`
  - `OnGetRecordCalendarAsync`
  - `OnGetRecordDayAsync`

### D. Update Tests & Verification
- Update `WebApplicationFactorySmokeTests.cs` to verify `matchpredictor-v3`.
- Add integration tests in `BetslipsRecordsDateTests.cs` verifying `OnGetLatestRecordDateAsync` for all sections and verifying proper cache headers.
- Verify that `dotnet test` passes across all test suites.

---

## 3. Step-by-Step Task Breakdown

### Phase 1: Service Worker Fix (`service-worker.js`)
- [x] Task 1.1: In `MatchPredictor.Web/wwwroot/service-worker.js`, bump `CACHE_NAME` from `'matchpredictor-v2'` to `'matchpredictor-v3'`.
- [x] Task 1.2: In `shouldBypass(url)`, exclude requests with `url.searchParams.has('handler')` or non-static asset destinations.
- [x] Task 1.3: In `fetch` event handler, ensure runtime caching only captures static assets (CSS, JS, images, fonts), leaving dynamic page requests to network-only.

### Phase 2: Client-side Script Hardening (`Betslips.cshtml`)
- [x] Task 2.1: In `fetchLatestDate(nextSection)`, add `cache: 'no-store'` and append `&_t=${Date.now()}` cache-busting parameter.
- [x] Task 2.2: In `fetchHtml(url)`, add `cache: 'no-store'` and append `_t=${Date.now()}` cache-busting parameter.
- [x] Task 2.3: In `loadSection(nextSection)`, replace flawed `remembered > latest ? remembered : latest` logic with `const date = latest || remembered;`.

### Phase 3: Server-side Cache Control Headers (`Betslips.cshtml.cs`)
- [x] Task 3.1: Add `Response.Headers.CacheControl = "no-cache, no-store, must-revalidate"`, `Pragma = "no-cache"`, `Expires = "0"` to `OnGetLatestRecordDateAsync`.
- [x] Task 3.2: Add identical no-cache response headers to `OnGetRecordCalendarAsync` and `OnGetRecordDayAsync`.

### Phase 4: Integration Testing & Verification
- [x] Task 4.1: Update `WebApplicationFactorySmokeTests.ServiceWorker_ReturnsNoCache_AndIncludesCurrentCacheName` to assert `matchpredictor-v3`.
- [x] Task 4.2: In `BetslipsRecordsDateTests.cs`, add tests for `OnGetLatestRecordDateAsync` verifying valid latest dates and no-cache headers for Banker, Rollover, Ladder, and AI Draws.
- [x] Task 4.3: Run full integration test suite: `DOTNET_CLI_HOME=/tmp dotnet test MatchPredictor.Tests.Integration/MatchPredictor.Tests.Integration.csproj --filter "FullyQualifiedName~Betslip|FullyQualifiedName~ServiceWorker"`.
- [x] Task 4.4: Run unit test suite: `DOTNET_CLI_HOME=/tmp dotnet test MatchPredictor.Tests.Unit/MatchPredictor.Tests.Unit.csproj`.

---

## 4. Verification Criteria
1. Switching between Banker, Rollover, Ladder, and AI Draws tabs on `/betslips` always selects the latest date for each section (e.g. Rollover -> `2026-09-28`, Banker -> `2026-09-27`, Ladder -> `2026-09-26`, AI Draws -> `2026-09-13`).
2. Stale dates from August 2026 never appear when switching tabs.
3. The calendar displays the current month (September 2026) for all sections that have current month records.
4. Service worker version is `matchpredictor-v3`, and dynamic handler requests bypass `CacheStorage`.
5. All automated unit and integration tests pass cleanly.
