# Plan: Upgrade Gemini Model to gemini-3.8-flash & Handle Legacy Model Aliasing

- **Slug:** `update-gemini-model`
- **Primary Agent:** `project-planner`
- **Collaborating Agents:** `backend-specialist`, `test-engineer`
- **Target Files:**
  - `MatchPredictor.Infrastructure/Services/Llm/AiLlmSettingsResolver.cs`
  - `MatchPredictor.Infrastructure/Services/Llm/AiLlmRouter.cs`
  - `MatchPredictor.Web/appsettings.Production.json`
  - `MatchPredictor.Web/appsettings.Development.json`
  - `MatchPredictor.Tests.Unit/AiLlmSettingsResolverTests.cs`
- **Status:** DRAFTED & READY FOR EXECUTION

---

## 1. Problem Statement & Root Cause

From the Railway deployment runtime logs:
```text
[21:19:53 INF] Calling LLM provider gemini model gemini-2.5-flash at https://generativelanguage.googleapis.com/v1beta/openai/chat/completions
  "error": {
    "message": "This model models/gemini-2.5-flash is no longer available to new users. Please update your code to use models/gemini-3.8-flash for the latest features and improvements. We recommend you to use the Interactions API (https://ai.google.dev/gemini-api/doc
    "code": 404,
[21:19:53 INF] Calling LLM provider openai model gpt-5.6-luna at https://api.openai.com/v1/chat/completions
[21:19:59 INF] Fallback LLM provider openai/gpt-5.6-luna succeeded after primary failure.
```

### Key Observations:
1. **Routing Fix Was Successful:** The primary call went to Google's endpoint, and the fallback cleanly called OpenAI's endpoint with `gpt-5.6-luna` and succeeded.
2. **Model Deprecation / Discontinuation:** Google AI Studio returned HTTP 404 because `models/gemini-2.5-flash` is no longer available to new accounts/users. Google explicitly advises:
   > *"Please update your code to use models/gemini-3.8-flash for the latest features and improvements."*
3. **Configuration & Code Defaults:**
   - `MatchPredictor.Web/appsettings.*.json` explicitly has `"Model": "gemini-2.5-flash"`.
   - `AiLlmRouter.cs` defaults null models to `"gemini-2.5-flash"` and `"gemini-2.5-pro"`.
   - `AiLlmSettingsResolver.cs` has `DefaultGeminiModel = "gemini-3.5-flash"`.

---

## 2. Solution Strategy

### A. Update Configuration Files
- In `MatchPredictor.Web/appsettings.Production.json` and `MatchPredictor.Web/appsettings.Development.json`:
  - Change `"Model": "gemini-2.5-flash"` to `"Model": "gemini-3.8-flash"`.

### B. Modernize Default Constants & Tier Routing
- In `AiLlmSettingsResolver.cs`:
  - Update `DefaultGeminiModel = "gemini-3.8-flash"`.
  - Add auto-remapping for discontinued models:
    - If a user still has `gemini-2.5-flash` or `gemini-1.5-flash` in an existing Railway environment variable (`AiLlm__Model`), automatically alias/upgrade it to `gemini-3.8-flash`.
    - If a user has `gemini-2.5-pro` or `gemini-1.5-pro`, automatically alias it to `gemini-3.8-pro`.
- In `AiLlmRouter.cs`:
  - Change default fast model fallback from `"gemini-2.5-flash"` to `"gemini-3.8-flash"`.
  - Change default deep reasoning fallback from `"gemini-2.5-pro"` to `"gemini-3.8-pro"`.
  - Ensure upshift/downshift logic (`flash` <-> `pro`) seamlessly works with `3.8`.

### C. Unit Testing & Verification
- In `AiLlmSettingsResolverTests.cs`:
  - Add test: `Resolve_Gemini_UpgradesDeprecatedGemini25Model_ToGemini38`
  - Add test: `Resolve_Gemini_UsesGemini38Flash_AsDefaultModel`
  - Ensure all existing unit and regression tests pass.
- Run `python3 .agent/scripts/checklist.py .`

---

## 3. Step-by-Step Task Breakdown

### Phase 1: Configuration Update
- [ ] Task 1.1: Update `MatchPredictor.Web/appsettings.Production.json` to `"gemini-3.8-flash"`.
- [ ] Task 1.2: Update `MatchPredictor.Web/appsettings.Development.json` to `"gemini-3.8-flash"`.

### Phase 2: Resolver & Router Modernization
- [ ] Task 2.1: Update `AiLlmSettingsResolver.DefaultGeminiModel` to `"gemini-3.8-flash"`.
- [ ] Task 2.2: Implement `NormalizeDeprecatedModel(provider, model)` in `AiLlmSettingsResolver` to smoothly migrate `gemini-2.5-flash` -> `gemini-3.8-flash`.
- [ ] Task 2.3: Update `AiLlmRouter.cs` default fallback strings to `"gemini-3.8-flash"` and `"gemini-3.8-pro"`.

### Phase 3: Unit Testing & Verification
- [ ] Task 3.1: Add unit tests in `AiLlmSettingsResolverTests.cs` for model normalization and default values.
- [ ] Task 3.2: Run `dotnet test MatchPredictor.Tests.Unit/MatchPredictor.Tests.Unit.csproj`.
- [ ] Task 3.3: Run `python3 .agent/scripts/checklist.py .`.

### Phase 4: Commit & Push
- [ ] Task 4.1: Commit all changes with message: `fix(ai): update default Gemini model to gemini-3.8-flash and alias deprecated models`.
- [ ] Task 4.2: Push to `origin/ai-plan`.

---

## 4. Verification Criteria
- `dotnet test` on unit tests completes with 0 failures.
- Running `Resolve()` with `AiLlm:Model = "gemini-2.5-flash"` yields `gemini-3.8-flash`.
- `checklist.py` passes all core checks (6/6).
