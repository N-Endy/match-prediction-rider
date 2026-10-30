# Implementation Plan: SportyClaw AI & Quick Slip Alignment

**Slug:** `sportyclaw-ai-and-quick-slip-alignment`  
**Status:** Completed  

## Overview
Aligned MatchPredictor's AI Chat and Quick Slip Co-Pilot with the natural, responsive, and accurate experience exemplified by SportyClaw AI:
1. Fixed Quick Slip leg count constraint (e.g. "4 BTTS" returns exactly 4 legs, not 6).
2. Fixed Quick Slip 1.00x combined odds bug by implementing a 3-tier odds resolver (`PredictionOddsSnapshots` -> `MatchDatas` -> Model Implied Odds).
3. Fixed "tonight" time windowing (filter for 17:00+ local kickoff on today's card with graceful evening fallback).
4. Upgraded AI Chat prompting, UI, and styling to produce SportyClaw-style natural conversational breakdowns, slate research context, instant booking codes, and data-backed pick rationales.

---

## Tasks

- [x] **Task 1: Update Quick Slip Client & Intent Parsing for Count & "Tonight"**
  - **Files:**
    - `MatchPredictor.Web/wwwroot/js/quick-slip.js`
    - `MatchPredictor.Domain/Models/QuickSlipParseModels.cs`
    - `MatchPredictor.Domain/Services/QuickSlipParser.cs`
  - **Changes:**
    - Ensured `quick-slip.js` passes `maxLegs: 10` as a general safety cap without overriding the user's explicit requested count.
    - Added `IsTonight` detection and enhanced general count regex in `QuickSlipParser`.
    - Unit tests added and verified for `"4 both teams to score tonight"`.

- [x] **Task 2: Implement 3-Tier Odds Resolver & Leg Count Prioritization in QuickSlipService**
  - **File:** `MatchPredictor.Infrastructure/Services/QuickSlipService.cs`
  - **Changes:**
    - Prioritized `intent.RequestedCount` over `requestedMaxLegs` so asking for 4 picks returns 4.
    - Restricted "tonight" matches to today's fixtures kicking off from $\ge$ 17:00 local time.
    - Implemented 3-tier odds resolution: queries `PredictionOddsSnapshots` for live SportyBet prices, falls back to `MatchDatas`, and uses `BetPricingMath.ConvertProbabilityToDecimalOdds` as a safe model floor.
    - Fixed combined odds multiplication so tickets never display `1.00x` or `@--`.

- [x] **Task 3: Align AI Chat Prompting & Persona with SportyClaw AI**
  - **Files:**
    - `MatchPredictor.Infrastructure/Services/AiAdvisorService.cs`
    - `MatchPredictor.Domain/Models/AiChatWorkingSlipSummary.cs`
    - `MatchPredictor.Web/Pages/AiChat.cshtml`
    - `MatchPredictor.Web/wwwroot/css/site.css`
  - **Changes:**
    - Updated system prompt with SportyClaw structure: ticket summary badge (`🎟️`), slate research context, booking code (`📋 Code:`), conviction tags (`BANKER`, `VALUE`, `SOLID`), and concrete statistical proof in quote blocks.
    - Added dynamic 6-character booking code generation (`MP-XXXXX`) to `AiChatWorkingSlipSummary`.
    - Enhanced frontend `formatMessageHtml` to render blockquotes and click-to-copy booking codes.
    - Added CSS styling for `.mp-chat-quote` and `.mp-copyable-code`.

- [x] **Task 4: Regression Testing & Final Checklist**
  - **Commands:**
    - `dotnet test MatchPredictor.Tests.Unit/MatchPredictor.Tests.Unit.csproj --no-build` (407/407 passed)
    - `dotnet test MatchPredictor.Tests.Integration/MatchPredictor.Tests.Integration.csproj --no-build --filter "FullyQualifiedName!~Investigate"` (459/459 passed)
    - `python3 .agent/scripts/checklist.py .` (6/6 passed)

