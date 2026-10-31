# Implementation Plan: AI Tipster & Bankroll Operations Cockpit

**Slug:** `ai-tipster-bankroll-operations`  
**Status:** Completed  

## Overview
Build a private, owner-only operational page (`/Admin/Operations`) dedicated to generating real-world betting profit through an institutional-grade AI Tipster:
1. **Interactive Bankroll & Dynamic Staking Engine:** Real-time stake sizing in chosen currency (`₦`, `$`, `£`, `€`) using Quarter-Kelly and edge-based bankroll allocation.
2. **3-Pillar Profit Portfolio:**
   - **Pillar 1 (The Compounder):** 1.50–2.25x odds, 60% daily stake budget, steady capital growth.
   - **Pillar 2 (Value Edge):** 3.20–6.50x odds, 30% daily stake budget, exploiting bookmaker mispricings (+EV).
   - **Pillar 3 (Multiplier Sniper):** 12.0x–35.0x odds, 10% daily stake budget, asymmetric upside with capped risk.
3. **Capital Preservation Protocol:** If a pillar has zero edge fixtures on a quiet slate, the AI marks it `🛡️ NO BET / CAPITAL PRESERVED` with ₦0 stake.
4. **SportyBet Auto-Booking & Executive Briefing:** Live booking codes with 1-click copy, plus an experienced tipster slate analysis detailing market traps and tactical angles.
5. **Security:** Protected by `[PrivateSelectionsAuth]` (returns 404 to any public request without passkey/cookie).

---

## Tasks

- [x] **Task 1: Domain Models & Service Interfaces**
  - **Files:**
    - `MatchPredictor.Domain/Models/OperationsTipsterModels.cs`
    - `MatchPredictor.Domain/Interfaces/IOperationsTipsterService.cs`
  - **Description:** Define `OperationsDossierDto`, `OperationsPillarTicketDto`, and `OperationsLegDto` along with service contracts.

- [x] **Task 2: Implement OperationsTipsterService & DI Wiring**
  - **Files:**
    - `MatchPredictor.Application/Services/OperationsTipsterService.cs`
    - `MatchPredictor.Web/Program.cs`
    - `MatchPredictor.Web/Middleware/AdminUsageBasicAuthMiddleware.cs`
  - **Description:** Implement slate analysis, portfolio filtering (Compounder, Value Edge, Sniper), Capital Preservation checks, Kelly stake weighting, and SportyBet booking. Register in DI and configure middleware passkey bypass.

- [x] **Task 3: Build `/Admin/Operations` Razor Page & Interactive UI**
  - **Files:**
    - `MatchPredictor.Web/Pages/Admin/Operations.cshtml`
    - `MatchPredictor.Web/Pages/Admin/Operations.cshtml.cs`
    - `MatchPredictor.Web/Pages/Admin/Selections.cshtml`
  - **Description:** Create the executive cockpit with interactive Bankroll & Currency bar, Slate Grade badge, Executive AI Briefing, and 3-Pillar ticket cards with live client-side stake recalculation and cross-nav pill.

- [x] **Task 4: Unit & Integration Tests**
  - **Files:**
    - `MatchPredictor.Tests.Integration/OperationsTipsterServiceTests.cs`
    - `MatchPredictor.Tests.Integration/OperationsPageTests.cs`
    - `MatchPredictor.Tests.Integration/PrivateSelectionsAuthTests.cs`
  - **Description:** Test portfolio composition, Capital Preservation rules, staking math, and passkey authorization.

- [x] **Task 5: Final Verification & Master Checklist**
  - **Commands:** Full solution build (0 errors), test suite execution (407 Unit tests passed, 465 Integration tests passed), and `.agent/scripts/checklist.py .` (all 6 checks passed).
