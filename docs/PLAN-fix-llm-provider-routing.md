# Plan: Fix Cross-Provider LLM Routing & Configuration Isolation

- **Slug:** `fix-llm-provider-routing`
- **Primary Agent:** `project-planner`
- **Collaborating Agents:** `backend-specialist`, `security-auditor`, `test-engineer`
- **Target Files:**
  - `MatchPredictor.Infrastructure/Services/Llm/AiLlmSettingsResolver.cs`
  - `MatchPredictor.Tests.Unit/AiLlmSettingsResolverTests.cs`
- **Status:** DRAFTED & READY FOR EXECUTION

---

## 1. Problem Statement & Root Cause Analysis

From the Railway deployment runtime error:
```
[20:42:18 INF] Calling LLM provider gemini model gemini-2.5-flash at https://api.openai.com/v1/chat/completions
[20:42:18 ERR] LLM provider gemini API error: Unauthorized { "message": "Incorrect API key provided: AIzaSyBn..." }
...
Falling back to openai/gpt-5.6-luna.
[20:42:18 INF] Calling LLM provider openai model gpt-5.6-luna at https://generativelanguage.googleapis.com/v1beta/openai/chat/completions
[20:42:18 ERR] LLM provider openai API error: BadRequest { "message": "Please pass a valid API key" }
```

### Root Causes:
1. **Cross-Provider BaseUrl Contamination:**
   In Railway, the user previously had `AiLlm__BaseUrl = https://api.openai.com/v1`. When `GEMINI_API_KEY` was added, `AiLlmSettingsResolver` resolved `provider = gemini`, but unconditionally preferred `configuredBaseUrl` over `presetBaseUrl`. Consequently, Gemini called `https://api.openai.com/v1` with an `AIzaSy...` key.
2. **Inverted Fallback BaseUrl & Provider Default:**
   In `ResolveFallbackSettings`, when `Fallback:Provider` was omitted, the resolver defaulted to `GeminiProvider`, but paired it with `gpt-5.6-luna` or inherited the Google preset BaseUrl, sending the OpenAI request to `generativelanguage.googleapis.com`.
3. **Missing Key-Provider Affinity Validation:**
   The resolver did not validate key signatures (`AIzaSy...` for Google, `sk-...` for OpenAI, `gsk_...` for Groq) against the destination endpoint, resulting in confusing 400/401 upstream errors instead of intelligent auto-alignment or clear diagnostic logs.

---

## 2. High-Level Architecture & Fix Strategy

```
                                 [ Incoming Config / Env Vars ]
                                               │
                                               ▼
                         ┌───────────────────────────────────────────┐
                         │      AiLlmSettingsResolver.Resolve()      │
                         └─────────────────────┬─────────────────────┘
                                               │
                       ┌───────────────────────┴───────────────────────┐
                       ▼                                               ▼
         [ Primary Resolution ]                             [ Fallback Resolution ]
       - Inspect Key Prefix & Model                       - Inspect Key Prefix & Model
       - Auto-detect Provider if mismatched               - Auto-detect Provider if mismatched
       - Discard Cross-Provider BaseUrl                   - Discard Cross-Provider BaseUrl
         (e.g., openai url for gemini)                      (e.g., gemini url for openai)
                       │                                               │
                       ▼                                               ▼
         ┌───────────────────────────┐                   ┌───────────────────────────┐
         │ Gemini:                   │                   │ OpenAI:                   │
         │ - Key: AIzaSy...          │                   │ - Key: sk-...             │
         │ - BaseUrl: googleapis.com │                   │ - BaseUrl: openai.com     │
         │ - Model: gemini-2.5-flash │                   │ - Model: gpt-5.6-luna     │
         └───────────────────────────┘                   └───────────────────────────┘
```

### Key Technical Enhancements:
1. **BaseUrl Provider Affinity Guard:**
   If `configuredBaseUrl` contains a domain known to belong to a different provider (e.g. `api.openai.com` when provider is `gemini`, or `googleapis.com` when provider is `openai`), ignore the stale configured URL and use the correct provider preset URL.
2. **Key Prefix Auto-Detection:**
   - Keys starting with `AIzaSy` automatically bind to `gemini`.
   - Keys starting with `sk-` (not `gsk_`) automatically bind to `openai`.
   - Keys starting with `gsk_` automatically bind to `groq`.
3. **Symmetrical Fallback Provider Resolution:**
   Apply the exact same provider and URL resolution logic to the fallback configuration block so fallback never cross-wires models and URLs.

---

## 3. Task Breakdown

### Task 1: Add Key & Domain Affinity Guards to `AiLlmSettingsResolver.cs`
- **Agent:** `backend-specialist`
- **Skills:** `clean-code`, `api-patterns`
- **Priority:** P0 (Blocker)
- **Input:** `AiLlmSettingsResolver.cs`
- **Output:**
  - `IsCrossProviderUrlContaminated(string provider, string baseUrl)` helper.
  - Key prefix detection helper `DetectProviderFromKey(string key)`.
  - Symmetrical `ResolveProvider(...)` applied to both primary and fallback endpoints.
- **Verify:** `dotnet build MatchPredictor.Infrastructure` succeeds with 0 warnings.

### Task 2: Unit Test Suite for URL & Key Auto-Alignment
- **Agent:** `test-engineer`
- **Skills:** `testing-patterns`, `tdd-workflow`
- **Priority:** P1
- **Input:** `MatchPredictor.Tests.Unit/AiLlmSettingsResolverTests.cs`
- **Output:**
  - Test: Discards OpenAI BaseUrl when Gemini key/provider is configured.
  - Test: Discards Gemini BaseUrl when OpenAI key/provider is configured.
  - Test: Auto-detects Gemini provider when key begins with `AIzaSy`.
  - Test: Auto-detects OpenAI provider when key begins with `sk-`.
  - Test: Fallback correctly isolates URL from primary URL.
- **Verify:** `dotnet test MatchPredictor.Tests.Unit --filter "FullyQualifiedName~AiLlmSettingsResolverTests"` passes 100%.

### Task 3: Railway Environment Variable Cleanup Documentation
- **Agent:** `project-planner`
- **Priority:** P1
- **Input:** Railway environment variable configuration
- **Output:** Clear guidance for user to remove stale `AiLlm__BaseUrl` in Railway dashboard.
- **Verify:** Documentation delivered in chat.

---

## 4. Phase X: Verification Checklist

- [ ] All unit tests in `AiLlmSettingsResolverTests` pass.
- [ ] Full solution compiles cleanly (`dotnet build`).
- [ ] No regression in full test suite (`dotnet test`).
- [ ] Python master checklist (`checklist.py`) passes 6/6.
- [ ] Changes committed and pushed to `ai-plan`.
