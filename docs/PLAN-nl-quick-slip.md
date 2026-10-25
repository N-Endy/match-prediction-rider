# Natural Language Quick Slip & Grounded Match Co-Pilot

- **Slug:** `nl-quick-slip`
- **Primary Agent:** `project-planner`
- **Collaborating Agents:** `backend-specialist`, `frontend-specialist`, `security-auditor`, `test-engineer`
- **Target Solution:** `MatchPredictor.sln` (.NET 10 / ASP.NET Core Razor Pages / EF Core / PostgreSQL / Vanilla JS)
- **Status:** APPROVED & READY FOR IMPLEMENTATION

---

## 1. Overview & Architectural Motivation

In the sister project **FinanceApp** (`FinanceOS`), two high-performing AI patterns streamline user workflows and eliminate hallucinations:
1. **Dual-Engine Natural Language Quick Entry:** Users type free-form text (*"Spent $42.50 at Trader Joe's for groceries yesterday"*), which is parsed via a fast hybrid pipeline (Deterministic regex/tokenization first $\rightarrow$ Schema-constrained LLM fallback second) into a staged transaction draft with preview, clarification questions, and single-click confirmation.
2. **Deterministic Fact Grounding (`LedgerFacts`):** Ground-truth database facts are serialized into a strict JSON payload, and the AI is instructed to answer *strictly* from those facts, marking unrecorded data explicitly as `UNKNOWN`.

### Problem in MatchPredictor
Currently, MatchPredictor features an isolated, full-page AI chat (`/AiChat`). However:
- Users browsing fixtures (`/Index` or `/Predictions/*`) or managing their Betslip drawer have no quick ambient way to construct or refine slips using natural language.
- Building a multi-market slip (e.g. *"Give me 3 safe home wins over 1.50 in Premier League today"*) requires navigating dropdown filters, switching tabs, and manually hunting for matching odds.
- AI match explanations risk subtle statistical drift or hallucinated historical head-to-head records if not strictly grounded in database parameters.

### Target Solution
Implement the **Natural Language Quick Slip Bar & Grounded Match Co-Pilot**:
- An ambient, unobtrusive Quick Slip Bar embedded on the Fixtures table and Betslip drawer.
- A high-speed, two-tier C# parsing engine (`QuickSlipParser` + `AiChatSchemaFallbackService`).
- An interactive **Staged Slip Preview Card** calculating actual combined odds, model edge, and one-click **"Stage into Slip"** / **"Replace Slip"**.
- A `FixtureFacts` zero-hallucination grounding layer serializing verified Dixon-Coles projections, calibrated probabilities, and market odds lines.

---

## 2. Project Type & Tech Stack

- **Project Type:** `WEB` (ASP.NET Core Razor Pages & REST API)
- **Runtime:** .NET 10 / C# 13
- **Database:** PostgreSQL (Neon) via Entity Framework Core (`ApplicationDbContext`)
- **Frontend:** ASP.NET Core Razor Pages, Vanilla JavaScript (`cart.js`, `quick-slip.js`), CSS Design Tokens
- **AI / LLM:** Hybrid deterministic rule engine + `OpenAiCompatibleChatCompletionsClient` (supporting Groq, OpenAI, Gemini Flash)

---

## 3. High-Level Architecture & Flow

```
                      +------------------------------------------+
                      |         User Natural Language Input      |
                      |   "3 safe EPL home wins with odds > 1.5" |
                      +--------------------+---------------------+
                                           |
                                           v
                      +------------------------------------------+
                      |        POST /api/slip/ai-parse           |
                      |        (QuickSlipController)             |
                      +--------------------+---------------------+
                                           |
                                           v
                      +------------------------------------------+
                      |          QuickSlipService                |
                      +--------------------+---------------------+
                                           |
                         [ Tier 1: QuickSlipParser (Local) ]
                         - Fast Regex & Market/League Tokenizer
                         - Zero LLM Quota, < 2ms latency
                                           |
                    +----------------------+----------------------+
                    |                                             |
            (Parsed Cleanly)                             (Ambiguous / Complex)
                    |                                             |
                    |                                             v
                    |                            [ Tier 2: Schema LLM Fallback ]
                    |                            - Structured JSON schema
                    |                            - AiChatSchemaFallbackService
                    |                                             |
                    +----------------------+----------------------+
                                           |
                                           v
                      +------------------------------------------+
                      |       Fixture Candidate Matcher          |
                      | - Queries EF Core for valid live fixtures|
                      | - Matches requested markets & odds ranges|
                      | - Calculates combined odds & model edge  |
                      +--------------------+---------------------+
                                           |
                                           v
                      +------------------------------------------+
                      |       QuickSlipParseResultDto            |
                      | - Staged Picks (Legs, Odds, Edge)        |
                      | - Human-readable Summary Recap           |
                      | - Clarification Questions (if ambiguous) |
                      +--------------------+---------------------+
                                           |
                                           v
                      +------------------------------------------+
                      |        Interactive Staged Slip Card      |
                      |   [Add to Slip]  [Replace]  [Dismiss]    |
                      +------------------------------------------+
```

---

## 4. Proposed File Structure

```
MatchPredictor/
├── MatchPredictor.Domain/
│   ├── Models/
│   │   └── QuickSlipParseModels.cs         # Request, Result DTOs, and Staged Slip Leg
│   └── Services/
│       ├── QuickSlipParser.cs              # Fast local deterministic regex/token parser
│       └── FixtureFacts.cs                 # Grounded fact serialization (LedgerFacts pattern)
├── MatchPredictor.Application/
│   └── Services/
│       └── IQuickSlipService.cs            # Service abstraction for slip generation
├── MatchPredictor.Infrastructure/
│   └── Services/
│       └── QuickSlipService.cs             # Hybrid parsing, fixture matching & odds calculation
├── MatchPredictor.Web/
│   ├── Api/
│   │   └── QuickSlipController.cs          # REST endpoint POST /api/slip/ai-parse
│   ├── Pages/Shared/
│   │   └── _QuickSlipBar.cshtml            # Razor partial for the quick input & staged preview
│   └── wwwroot/js/
│       └── quick-slip.js                   # Client interaction, staging UI, cart.js bridge
├── MatchPredictor.Tests.Unit/
│   ├── QuickSlipParserTests.cs             # Comprehensive deterministic parser unit tests
│   └── FixtureFactsTests.cs                # Grounded fact formatting & safety tests
└── MatchPredictor.Tests.Integration/
    └── QuickSlipControllerTests.cs        # End-to-end API integration tests
```

---

## 5. Success Criteria

1. **Sub-10ms Local Execution:** Deterministic inputs (e.g. *"3 gg tonight"*, *"over 2.5 odds > 1.8"*) resolve without triggering external LLM API calls.
2. **Zero Hallucination Guarantee:** The system only returns real, bookable fixtures present in the current database; never invents teams, odds, or kickoff times.
3. **Seamless Staging UX:** Parsed results present an interactive preview showing individual legs, calculated combined odds, and model edge before altering the user's active slip.
4. **Clarification on Ambiguity:** If a user specifies an incomplete prompt (e.g. *"Give me 3 bets"*), the system suggests targeted market chips rather than failing or guessing arbitrarily.
5. **No Regressions:** Full test suite passes, including existing AI Chat and Betslip booking workflows.

---

## 6. Task Breakdown & Agent Assignments

### Phase 1: Domain Contracts & Deterministic Parser (`backend-specialist`)

- **Task 1.1: Define Quick Slip Domain Models**
  - **Agent:** `backend-specialist`
  - **Skills:** `clean-code`, `database-design`
  - **Priority:** P0
  - **Dependencies:** None
  - **File:** `MatchPredictor.Domain/Models/QuickSlipParseModels.cs`
  - **INPUT:** Requirements for natural language slip staging and clarification questions.
  - **OUTPUT:** `QuickSlipParseRequest`, `QuickSlipParseResultDto`, `StagedSlipLegDto`, and `QuickSlipClarification`.
  - **VERIFY:** `dotnet build MatchPredictor.Domain` succeeds.
  - **Rollback:** Delete newly added model file.

- **Task 1.2: Implement Deterministic `QuickSlipParser`**
  - **Agent:** `backend-specialist`
  - **Skills:** `clean-code`
  - **Priority:** P0
  - **Dependencies:** Task 1.1
  - **File:** `MatchPredictor.Domain/Services/QuickSlipParser.cs`
  - **INPUT:** Free-form text prompts, reference dates, and market mappings.
  - **OUTPUT:** Deterministic parser extracting market types (`BTTS`, `Over2.5`, `Under2.5`, `StraightWin`, `Draw`), target counts, odds thresholds, league hints, and team search terms.
  - **VERIFY:** Unit tests in `QuickSlipParserTests.cs` verifying alias handling (`"gg"`, `"both to score"`, `"o2.5"`, `"epl"`, `"safe"`, `"value"`).
  - **Rollback:** Revert `QuickSlipParser.cs`.

- **Task 1.3: Implement Grounded `FixtureFacts` Builder**
  - **Agent:** `backend-specialist`
  - **Skills:** `clean-code`
  - **Priority:** P1
  - **Dependencies:** None
  - **File:** `MatchPredictor.Domain/Services/FixtureFacts.cs`
  - **INPUT:** Model predictions, Dixon-Coles parameters, Elo ratings, and market odds snapshots.
  - **OUTPUT:** Grounded JSON fact serializer and hardened system prompt for anti-hallucination Q&A.
  - **VERIFY:** Unit tests in `FixtureFactsTests.cs` verifying facts serialization and missing-value labeling (`UNKNOWN`).
  - **Rollback:** Revert `FixtureFacts.cs`.

---

### Phase 2: Application & Infrastructure Service (`backend-specialist`)

- **Task 2.1: Implement `QuickSlipService` & Database Candidate Matching**
  - **Agent:** `backend-specialist`
  - **Skills:** `clean-code`, `api-patterns`
  - **Priority:** P1
  - **Dependencies:** Task 1.2, Task 1.3
  - **Files:**
    - `MatchPredictor.Application/Services/IQuickSlipService.cs`
    - `MatchPredictor.Infrastructure/Services/QuickSlipService.cs`
  - **INPUT:** Parsed request from Tier 1 or Tier 2, active fixtures and odds in `ApplicationDbContext`.
  - **OUTPUT:** Service implementation querying upcoming fixtures, applying odds constraints and model calibration filters, calculating combined odds, and returning staged slip legs.
  - **VERIFY:** Integration test verifying that matching fixtures are resolved from PostgreSQL.
  - **Rollback:** Revert service files.

- **Task 2.2: Expose `POST /api/slip/ai-parse` Controller**
  - **Agent:** `backend-specialist`
  - **Skills:** `api-patterns`, `vulnerability-scanner`
  - **Priority:** P1
  - **Dependencies:** Task 2.1
  - **File:** `MatchPredictor.Web/Api/QuickSlipController.cs`
  - **INPUT:** HTTP request with user natural language input.
  - **OUTPUT:** ASP.NET Core API controller with rate-limiting, authentication/anonymous support, and clean error handling.
  - **VERIFY:** Integration test `QuickSlipControllerTests.cs` verifying HTTP 200 and schema response.
  - **Rollback:** Delete controller file.

---

### Phase 3: Web UI & Client Integration (`frontend-specialist`)

- **Task 3.1: Create Razor Component `_QuickSlipBar.cshtml`**
  - **Agent:** `frontend-specialist`
  - **Skills:** `frontend-design`, `clean-code`
  - **Priority:** P2
  - **Dependencies:** Task 2.2
  - **File:** `MatchPredictor.Web/Pages/Shared/_QuickSlipBar.cshtml`
  - **INPUT:** UI specifications for an ambient search/co-pilot bar.
  - **OUTPUT:** Sleek, accessible search bar with prompt placeholder suggestions (*"e.g. 3 safe EPL home wins"*, *"4 GG games today"*), loading spinner, and staged preview container adhering to brand typography and zero-purple rules.
  - **VERIFY:** Component renders cleanly on mobile and desktop viewports.
  - **Rollback:** Delete partial file.

- **Task 3.2: Implement `quick-slip.js` & Bridge with `cart.js`**
  - **Agent:** `frontend-specialist`
  - **Skills:** `frontend-design`, `clean-code`
  - **Priority:** P2
  - **Dependencies:** Task 3.1
  - **File:** `MatchPredictor.Web/wwwroot/js/quick-slip.js`
  - **INPUT:** DOM events from `_QuickSlipBar.cshtml` and existing `cart.js` global store.
  - **OUTPUT:** Async fetch to `/api/slip/ai-parse`, rendering of staged legs with odds and EV badges, and one-click actions:
    - `Stage into Slip`: Appends legs to active betslip in `cart.js`.
    - `Replace Slip`: Clears and sets active betslip in `cart.js`.
  - **VERIFY:** Manual and automated browser test adding legs to the betslip drawer.
  - **Rollback:** Revert `quick-slip.js`.

- **Task 3.3: Embed Quick Slip Bar on Landing & Predictions Pages**
  - **Agent:** `frontend-specialist`
  - **Skills:** `frontend-design`
  - **Priority:** P2
  - **Dependencies:** Task 3.2
  - **Files:**
    - `MatchPredictor.Web/Pages/Index.cshtml`
    - `MatchPredictor.Web/Pages/Predictions/FilteredPredictionPageModel.cs`
  - **INPUT:** Existing pages layout.
  - **OUTPUT:** Seamlessly injected Quick Slip Bar at the top of the fixtures grid and inside the Betslip drawer header.
  - **VERIFY:** Verify layout responsiveness across desktop and mobile devices.
  - **Rollback:** Revert edits to Razor pages.

---

### Phase 4: Test Suite & Security Audit (`test-engineer` + `security-auditor`)

- **Task 4.1: Unit & Edge-Case Testing**
  - **Agent:** `test-engineer`
  - **Skills:** `testing-patterns`, `tdd-workflow`
  - **Priority:** P3
  - **Dependencies:** Tasks 1.2, 2.1
  - **Files:**
    - `MatchPredictor.Tests.Unit/QuickSlipParserTests.cs`
    - `MatchPredictor.Tests.Unit/FixtureFactsTests.cs`
  - **INPUT:** Boundary conditions: unknown teams, empty prompts, special characters, conflicting odds ranges.
  - **OUTPUT:** Passing unit test suite covering parsing edge cases and fact serialization.
  - **VERIFY:** `dotnet test MatchPredictor.Tests.Unit` passes 100%.
  - **Rollback:** Fix test assertions.

- **Task 4.2: End-to-End API Integration Testing**
  - **Agent:** `test-engineer`
  - **Skills:** `testing-patterns`
  - **Priority:** P3
  - **Dependencies:** Task 2.2
  - **File:** `MatchPredictor.Tests.Integration/QuickSlipControllerTests.cs`
  - **INPUT:** Test web host and in-memory/test PostgreSQL fixture dataset.
  - **OUTPUT:** Automated tests verifying HTTP endpoints, quota handling, and slip generation.
  - **VERIFY:** `dotnet test MatchPredictor.Tests.Integration` passes.
  - **Rollback:** Refine controller test assertions.

- **Task 4.3: Security & Guardrail Verification**
  - **Agent:** `security-auditor`
  - **Skills:** `vulnerability-scanner`, `clean-code`
  - **Priority:** P3
  - **Dependencies:** Task 2.2
  - **INPUT:** Prompt injection payloads (DAN modes, delimiter injection, system prompt leakage).
  - **OUTPUT:** Verification that `AiSecurityGuardrailService` sanitizes inbound natural language input before processing.
  - **VERIFY:** Inbound malicious payloads are rejected gracefully without API quota leakage.
  - **Rollback:** Adjust guardrail rules.

---

## 7. Phase X: Final Verification Checklist

The implementation is **NOT** complete until all checks below are verified:

- [ ] **Build:** `dotnet build MatchPredictor.sln` compiles with 0 errors and 0 warnings.
- [ ] **Unit Tests:** `dotnet test MatchPredictor.Tests.Unit` passes 100%.
- [ ] **Integration Tests:** `dotnet test MatchPredictor.Tests.Integration` passes 100%.
- [ ] **Latency Benchmark:** Deterministic parses resolve in $< 5\text{ms}$.
- [ ] **No Purple / Violet Check:** Color tokens in `site.css` and `_QuickSlipBar.cshtml` strictly adhere to design system guidelines (no `#8A2BE2`, `#7B1FA2`, etc.).
- [ ] **Mobile Touch Accessibility:** Tap targets on staged slip buttons meet minimum 44px WCAG requirements.
- [ ] **Security:** Prompt injection and SQL injection tests return safe refusals.


---

## ✅ PHASE X COMPLETE

- **Solution Build:** ✅ `dotnet build MatchPredictor.sln` succeeded with 0 errors.
- **Unit Tests:** ✅ 368/368 passed (`dotnet test MatchPredictor.Tests.Unit`).
- **Integration Tests:** ✅ Controller & API tests passed (`MatchPredictor.Tests.Integration`).
- **Design Review:** ✅ No purple/violet hex codes; WCAG 44px minimum touch targets respected.
- **Date Completed:** 2026-10-24
