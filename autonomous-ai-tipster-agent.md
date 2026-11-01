# Implementation Plan: Autonomous AI Tipster Agent ("A Mind of Its Own")

**Slug:** `autonomous-ai-tipster-agent`  
**Status:** Completed  

## 1. Executive Summary
Transform `/Admin/Operations` from a static rule-based script into a **Hybrid Neuro-Symbolic Autonomous Tipster Agent**. 
The agent possesses authentic reasoning capabilities (powered by `IChatCompletionsClient` with JSON mode), analyzing match narratives, tactical scripts, and motivation. It dynamically decides the slate structure (1, 2, or 3 tickets), calculates Kelly risk weighting, and writes master tipster commentary while strictly bounded by mathematical safety guardrails and live SportyBet booking compatibility.

```
                      ┌───────────────────────────────────────┐
                      │    Daily Fixtures & Live DB Odds      │
                      │  (SportyBet Snapshots + MatchDatas)   │
                      └──────────────────┬────────────────────┘
                                         │
                                         ▼
                      ┌───────────────────────────────────────┐
                      │    Autonomous AI Tipster Agent        │
                      │  (IChatCompletionsClient + JSON Mode) │
                      │  - Qualitative Game-State Scripts     │
                      │  - Slate Grading & Trap Recognition   │
                      │  - Adaptive Slip Architecture (1-3)   │
                      └──────────────────┬────────────────────┘
                                         │
                                         ▼
                      ┌───────────────────────────────────────┐
                      │    Mathematical Guardrail Validator   │
                      │  - Verified Bookmaker Odds & Math     │
                      │  - Max Risk Exposure Floor (≤ 7.5%)   │
                      │  - Market Whitelist (Fast Booking)    │
                      └──────────────────┬────────────────────┘
                                         │
                                         ▼
                      ┌───────────────────────────────────────┐
                      │    SportyBet Auto-Booking Service     │
                      │  - Books Real Live Slips on Demand    │
                      └──────────────────┬────────────────────┘
                                         │
                                         ▼
                      ┌───────────────────────────────────────┐
                      │   /Admin/Operations Owner Cockpit     │
                      │  - Live Interactive Bankroll Engine   │
                      │  - Bespoke Tactical Rationale Cards   │
                      └───────────────────────────────────────┘
```

---

## 2. Core Architectural Pillars

### Pillar A: Neuro-Symbolic Synthesis
- **Qualitative Reasoning (The "Brain"):** The LLM reviews the daily card, team dynamics, form, and tactical styles. It recognizes traps (e.g. over-hyped away favorites, squad fatigue, derby volatility).
- **Symbolic Mathematical Guardrails (The "Brakes"):**
  - **Odds Verification:** The agent can only select odds verified against real `PredictionOddsSnapshots` or scraped `MatchDatas`.
  - **Exposure Bounds:** Total daily stake allocation cannot exceed **7.5%** of bankroll. No single slip can exceed **5.0 units**.
  - **Deterministic Fallback:** If the LLM provider fails, times out, or has invalid JSON, the service gracefully falls back to the existing deterministic math engine without crashing the page.

### Pillar B: Adaptive Portfolio Generation
- Instead of forcing 3 fixed boxes every day, the agent adapts to slate density:
  - **Grade A Slate (Prime weekend board):** Employs full 3-ticket offensive (Capital Compounder, Value Dislocation, and Multiplier Sniper).
  - **Grade B Slate (Midweek standard):** Typically 2 high-conviction tickets (e.g., a solid 1.95x Double and a 4.50x Value Slip).
  - **Grade C Slate (Cup rotation / Thin cards):** Restricts action to 1 targeted position or invokes **Capital Preservation** (₦0 stake).

### Pillar C: 100% Fast-Booked Market Whitelist
Restricted exclusively to markets supported by `ISportyBetBookingService`:
- `1X2` (Home Win, Away Win, Draw)
- `Over/Under` (Over 1.5, Over 2.5, Over 3.5, Under 2.5, Under 3.5)
- `Both Teams to Score` (Yes / No)
- `Double Chance` (1X, X2, 12)

---

## 3. Detailed Task Breakdown

- [x] **Phase 1: Agent Prompt Design & Response Schema**
  - **File:** `MatchPredictor.Application/Prompts/AutonomousTipsterPrompt.cs`
  - Defined the system prompt for the Master Quant Tipster persona with qualitative game-state modeling, xG differential, and bankroll discipline.
  - Defined typed JSON schema (`AutonomousSlateLlmResponse`, `AutonomousTicketLlmDto`) in `OperationsTipsterModels.cs`.

- [x] **Phase 2: Autonomous Tipster Agent Implementation**
  - **Files:**
    - `MatchPredictor.Domain/Interfaces/IAutonomousTipsterAgentService.cs`
    - `MatchPredictor.Application/Services/AutonomousTipsterAgentService.cs`
  - Implemented end-to-end reasoning engine with `IChatCompletionsClient`, JSON mode parsing, candidate serialization, mathematical guardrail validation, SportyBet auto-booking, and deterministic fallback.

- [x] **Phase 3: Wire into Operations Page & UI Polish**
  - **Files:**
    - `MatchPredictor.Application/Services/OperationsTipsterService.cs`
    - `MatchPredictor.Web/Program.cs`
    - `MatchPredictor.Web/Pages/Admin/Operations.cshtml`
  - Registered `IAutonomousTipsterAgentService` in DI. Connected `OperationsTipsterService` to attempt LLM reasoning first and fallback on failure. Added `EngineMode` pill (`🧠 Autonomous Reasoning Agent` vs `⚡ Algorithmic Fallback Engine`) to `/Admin/Operations`.

- [x] **Phase 4: Unit & Integration Verification**
  - **Files:**
    - `MatchPredictor.Tests.Unit/AutonomousTipsterAgentTests.cs`
  - Verified JSON schema parsing, 7.5% risk clamping, fixture deduplication, capital preservation handling, and LLM timeout handling.
  - Test suites: 411 Unit tests passed (0 failed), 465 Integration tests passed (0 failed). All 6 Antigravity checklist steps passed.

---

## 4. Verification & Success Criteria
1. **0 Build Errors & 100% Test Pass Rate:** 411 unit tests passed, 465 integration tests passed.
2. **True Adaptive Logic:** The AI autonomously downsizes to 1 or 2 tickets instead of blindly forcing 3 when appropriate.
3. **Verified Mathematical Safety:** Total daily stakes never exceed 7.5% under any prompt conditions.
4. **Valid SportyBet Booking:** Every active ticket generates a valid, loadable SportyBet booking code.
