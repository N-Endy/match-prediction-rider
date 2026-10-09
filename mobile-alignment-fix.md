# Mobile Alignment & Text Overflow Remediation Plan

## Goal
Fix mobile view alignment, layout blowout, and text overflowing across all pages (especially Results and Analytics), ensuring responsive design down to 320px/360px smartphones.

## Tasks

### Phase 1: Global Viewport & Container Foundation
- [x] Update `site.css` `.mp-container`: Adjust mobile padding from 24px (1.5rem) to 16px (1rem) on screens <= 640px, and 12px (0.75rem) on screens <= 360px so mobile content gets maximum safe width without edge bleed.
- [x] Add global horizontal overflow guard (`overflow-x: clip;` / `overflow-x: hidden`) on `html, body, main` to eliminate viewport horizontal scrolling.

### Phase 2: Results Page Responsive Layout
- [x] In `site.css`, update `.mp-results-day-header`: On screens <= 640px, transform into a two-tier layout where date heading + relative tag sits on the top tier and hit rate badge + pick count + chevron wrap cleanly onto the second tier.
- [x] In `site.css`, update `.mp-results-summary`: Use `repeat(auto-fit, minmax(130px, 1fr))` on mobile, dropping to 2-column or 1-column gracefully without exceeding container width on 320px screens.
- [x] In `site.css`, update `.mp-results-toolbar`, `.mp-toolbar-row`, and `.mp-search-wrap`: Allow `.mp-search-wrap` to take full width (`min-width: 0; width: 100%`) when wrapped under the filter pills on mobile.
- [x] In `site.css`, update `.mp-mobile-card` and `.mp-mobile-teams`: Ensure `.mp-mobile-teams` has `min-width: 0; overflow-wrap: break-word;` so long team names do not push the score badge off-screen.
- [x] Ensure `.mp-mobile-card-bottom` handles long market/pick text without overflowing outcome badge.

### Phase 3: Analytics Page Responsive Overhaul
- [x] In `Analytics.cshtml` `<style>` and `site.css`:
  - Fix `.mp-market-card`: Add `min-width: 0; max-width: 100%;` so child tables do not force card expansion.
  - Fix `.mp-segment-table`: Add `max-width: 100%; overflow-x: auto; -webkit-overflow-scrolling: touch;`.
  - Fix `.mp-segment-row`: Keep min-width 640px inside the scrollable container, but make the first column sticky (`position: sticky; left: 0; background: var(--bg-card); z-index: 2; box-shadow: 4px 0 10px rgba(0,0,0,0.45);`) so users can scroll data columns while keeping league/market names visible.
  - Add visual horizontal scroll shadow/indicator on mobile for `.mp-segment-table`.
  - Fix `.mp-analytics-mode-tabs` and `.mp-analytics-tabs`: Improve mobile wrap and touch target padding so tab rows don't look broken.
  - Fix `.mp-stat-grid` and `.mp-stat-card`: Allow `minmax(140px, 1fr)` or full width, scale down large `font-size: 2.5rem` to `1.8rem` on small mobile screens to prevent value blowout.
  - Fix `.mp-cat-header` and `.mp-diag-summary`: Ensure `min-width: 0; word-break: break-word;` on long fixture and league names.

### Phase 4: Betslips, Predictions, Guides, Match Detail & Navigation
- [x] In `site.css`, update `.mp-betslip-grid` and `.mp-guides-grid`: Change `minmax(320px, 1fr)` to `minmax(min(100%, 280px), 1fr)` so grid items never exceed screen width on 320px-360px phones.
- [x] In `site.css`, update `.mp-betslip-selection-main` and `.mp-betslip-selection-fixture`: Add `min-width: 0; word-break: break-word;` so long fixtures wrap gracefully beside badges.
- [x] In `site.css`, update `.mp-betslip-code-row`: Stack code and buttons cleanly on <= 420px screens.
- [x] In `site.css`, update `.mp-prediction-card`: Expand mobile column layout to apply at `max-width: 768px` instead of only `640px` (avoiding the cramped tablet/landscape mobile gap), and lay out `.mp-card-badges` as a clean flex-wrap row with space-between.
- [x] In `site.css`, update `.mp-match-link`: Add `flex-wrap: wrap; word-break: break-word;` to prevent long match names from overflowing.
- [x] In `site.css`, update `.mp-stats-comparison-grid` and `.mp-market-cards-grid`: Use `minmax(min(100%, 280px), 1fr)`.
- [x] In `site.css`, update `.mp-footer` and `.mp-footer-trust`: Ensure proper line breaks and padding on narrow mobile screens.

### Phase 5: Verification & Quality Assurance
- [x] Run `dotnet build` to ensure all Razor pages and syntax compile without error.
- [x] Verify unit tests pass (`dotnet test`).

