# Implementation Plan: Private Synergy Selections Page & Exploitation Betslips

**Task:** Create a private "Selections" page with 6 automated betslips exploiting high-confidence cross-market synergies discovered in historical analysis.  
**Plan File:** `private-selections-page.md`  
**Status:** COMPLETED - All Tasks Implemented & Verified

---

## 1. Feature Overview & Objectives

Deliver an internal, private "Selections" page accessible only to the site owner, featuring 6 algorithmic betslips generated from 4 proven statistical synergy patterns:

1. **Slip 1 ("Under Fortress"):** `Under 2.5` on fixtures with $\text{Draw} \ge 0.35$ and $\text{Under 2.5} \ge 0.65$ (Empirical win rate: **69.52%**).
2. **Slip 2 ("Favorite Machine - Win"):** `Straight Win` on fixtures with $\text{Win} \ge 0.75$ and $\text{Over 2.5} \ge 0.65$ (Empirical win rate: **82.49%**).
3. **Slip 3 ("Favorite Machine - Over 2.5"):** `Over 2.5` on the same fixtures as Slip 2 (Empirical win rate: **71.96%**).
4. **Slip 4 ("Goal Frenzy - Over 2.5"):** `Over 2.5` on fixtures with $\text{BTTS} \ge 0.60$ and $\text{Over 2.5} \ge 0.65$ (Empirical win rate: **70.49%**).
5. **Slip 5 ("Goal Frenzy - BTTS"):** `BTTS (Yes)` on the same fixtures as Slip 4 (Empirical win rate: **64.10%**).
6. **Slip 6 ("Draw/BTTS Strike"):** `BTTS (Yes)` on fixtures qualifying under Draw with $\text{BTTS} \ge 0.57$ (Empirical win rate: **62.20%** across 82 matches).

---

## 2. Architecture & Design Decisions

### A. Access Control & Security
- **Route:** `/Admin/Selections`.
- **Mechanism:** Passkey authorization (`AdminSettings:SelectionsPasskey` in configuration).
  - Passing `?key=YOUR_PASSKEY` validates the key and sets an HTTP-only secure cookie for 60 days.
  - Subsequent requests automatically authenticate via cookie.
  - Any request without a valid key/cookie returns HTTP `404 Not Found` (page is completely invisible to public visitors, search engines, and web scrapers).

### B. Generation & SportyBet Booking
- **Scheduled Pipeline:** Generated during daily betslip runs in `BetslipGenerationService` (at 04:45 and 13:00 local time).
- **SportyBet Codes:** Auto-booked via `ISportyBetBookingService` so booking codes and booking links are pre-populated.
- **On-Demand Manual Refresh:** A "Refresh / Book Now" button on the private page to re-run or re-book on demand if new fixtures are synced.
- **Strict Sizing:** Slips include strictly all fixtures meeting the thresholds. If zero matches qualify on a light day, the slip clearly displays a `"No qualifiers today"` banner.

---

## 3. Step-by-Step Implementation Breakdown

### Phase 1: Domain & Storage Models
- [x] **Task 1.1: Define Synergy Kinds & Constants**
  - Add `SynergySlipKind` constants and tier labels in `MatchPredictor.Domain/Helpers/BetslipKinds.cs`.
  - Add configuration model `SynergySlipSettings` in `MatchPredictor.Domain/Models/BetslipSettings.cs` for thresholds.
- [x] **Task 1.2: Add IsPrivate Discriminator to Betslips**
  - Add `IsPrivate` boolean property to `MatchPredictor.Domain/Models/Betslip.cs` (default `false`).
  - Add EF migration `AddPrivateBetslipFlag` to update the PostgreSQL schema without impacting public slips.
  - Applied migration to Neon database.

### Phase 2: Application Composer & Pipeline
- [x] **Task 2.1: Implement SynergyBetslipComposer**
  - Create `MatchPredictor.Application/Helpers/SynergyBetslipComposer.cs`.
  - Group candidate predictions by fixture and evaluate the 4 cross-market conditions against configured thresholds.
  - Build the 6 distinct slips with strict qualification and market assignment.
- [x] **Task 2.2: Integrate into BetslipGenerationService**
  - Extend `BetslipGenerationService.cs` to generate and book the 6 private slips into the database.
  - Ensure public `BetslipsModel` queries filter `p => !p.IsPrivate` to guarantee public slips remain untouched.

### Phase 3: Web UI & Security Filter
- [x] **Task 3.1: Passkey Authorization Filter**
  - Create `MatchPredictor.Web/Filters/PrivateSelectionsAuthAttribute.cs` (cookie-backed passkey validation with 404 response on failure).
  - Configure `AdminSettings:SelectionsPasskey` in `appsettings.json` and `appsettings.Development.json`.
  - Exempt `/admin/selections` from `AdminUsageBasicAuthMiddleware.cs` so it evaluates passkey/cookie directly.
- [x] **Task 3.2: Create Razor Page `/Admin/Selections`**
  - Create `MatchPredictor.Web/Pages/Admin/Selections.cshtml` and `Selections.cshtml.cs`.
  - Display the 6 betslips with odds, SportyBet booking code, 1-click copy button, combined confidence, and fixture details.
  - Implement an on-demand `OnPostRefreshAsync` handler for instant recalculation.

### Phase 4: Verification & Automated Tests
- [x] **Task 4.1: Unit & Integration Tests**
  - Test threshold filtering for all 6 slips in `MatchPredictor.Tests.Unit/SynergyBetslipComposerTests.cs` (6 passed).
  - Test access control and security gate in `MatchPredictor.Tests.Integration/PrivateSelectionsAuthTests.cs` (6 passed).
  - Regression test suite across all betslip tests in `MatchPredictor.Tests.Integration` (41 passed).
- [x] **Task 4.2: Full Suite & Regression Check**
  - Verified compilation and build across all solution projects (0 errors).
