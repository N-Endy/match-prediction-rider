# Plan: Remove "+Select" Button on Completed Matches and Decided Outcomes

- **Plan File:** `docs/PLAN-remove-completed-select.md`
- **Slug:** `remove-completed-select`
- **Project Type:** WEB (ASP.NET Core Razor Pages + Vanilla JS)
- **Primary Agent:** `project-planner`
- **Collaborating Agents:** `backend-specialist`, `frontend-specialist`, `test-engineer`
- **Key Skills:** `clean-code`, `api-patterns`, `frontend-design`, `testing-patterns`
- **Status:** COMPLETED & VERIFIED

---

## 1. Overview & Context

On match prediction cards and match detail pages, users can click **"+ Select"** (or **"+ Add to Selections"**) to add a prediction leg to their selection drawer (cart) for SportyBet booking code generation.

Currently, this button renders unconditionally across all states. As a result:
1. Completed matches (matches with settled scores/outcomes or concluded kickoff windows) still display the active "+Select" button.
2. In-play (live) matches where the predicted outcome has already come true (e.g. BTTS is 1-1, Over 2.5 has 3+ goals) or is already mathematically busted (e.g. Under 2.5 with 3+ goals) still display the active "+Select" button.

### Core Objectives
1. **Hide "+Select" on Ended Matches:** Any match that has completed/settled (or whose duration has elapsed) must not display the selection button.
2. **Hide "+Select" When Option Has Come True (or Failed):** In live matches, if the market outcome has already definitively hit (BTTS at 1-1+, Over 2.5 at 3+ goals) or is already impossible (Under 2.5 at 3+ goals), the button must be omitted. Pending live predictions that are still undecided remain selectable.
3. **Consistent UI Application:** Apply to:
   - Prediction cards rendered via `_PredictionCard.cshtml` across all category pages (`/predictions/btts`, `/predictions/over2`, `/predictions/straightwin`, `/predictions/draw`, `/predictions/under2`).
   - The featured pick CTA button (`mp-add-cart-btn mp-btn-lg`) in `Match/Index.cshtml`.
   - The multi-market breakdown cards (`mp-add-cart-btn mp-btn-sm`) in `Match/Index.cshtml`.
4. **Cart Drawer Guarding:** For matches already saved in a user's selection cart that subsequently complete or settle, display an "Ended" indicator badge and exclude them from the SportyBet booking code generation payload.

---

## 2. Success Criteria

- [x] All completed matches across `/predictions/*` and `/match/{id}` do not display the "+Select" button.
- [x] Live matches where the option has definitively come true (e.g., BTTS at 1-1, Over 2.5 at 2-1) do not display the "+Select" button.
- [x] Live matches where the option has definitively failed (e.g., Under 2.5 at 2-1) do not display the "+Select" button.
- [x] Live matches where the option is still undecided (e.g., BTTS at 1-0, Over 2.5 at 1-0) continue displaying the "+Select" button.
- [x] Pre-match upcoming fixtures continue displaying the "+Select" button normally.
- [x] If a match in the selection cart drawer has ended, it shows a settled badge in the drawer and is excluded from booking code generation.
- [x] Comprehensive unit tests cover all combinations of match statuses and market conditions.
- [x] Integration tests verify Razor rendering and `cart.js` execution without regressions.

---

## 3. Architecture & Technical Design

### A. Server-Side Decision Engine (`PredictionDisplayHelper.cs` / `PredictionScoreClassHelper.cs`)

Create a centralized helper method `CanAddToSelections(Prediction prediction, DateTime utcNow)`:

```csharp
public static bool CanAddToSelections(Prediction prediction, DateTime utcNow)
{
    // 1. Ended Check:
    // Match is settled if it has ActualScore, ActualOutcome, or its elapsed time exceeds 200m from kickoff without being live
    if (IsMatchEnded(prediction, utcNow))
    {
        return false;
    }

    // 2. Live Decision Check:
    // If live, verify whether the option is already decided (either won or busted)
    if (IsActuallyLive(prediction, utcNow))
    {
        if (HasLiveOptionDecided(prediction))
        {
            return false;
        }
    }

    return true;
}
```

#### Detailed Rule Matrix: `HasLiveOptionDecided(Prediction prediction)`
When a match is live and an actual live score is parsed (`homeGoals` and `awayGoals`):

| Market | Predicted Outcome | Live Score Example | Status | Decided? (`CanAddToSelections`) |
|---|---|---|---|---|
| **BTTS** | BTTS / Yes | 1 - 1, 2 - 1 | Already Won | `false` (hide button) |
| **BTTS** | BTTS / Yes | 1 - 0, 0 - 0 | Pending | `true` (show button) |
| **BTTS** | No BTTS / No | 1 - 1 | Already Lost | `false` (hide button) |
| **Over 2.5** | Over 2.5 | 2 - 1, 3 - 0 | Already Won | `false` (hide button) |
| **Over 2.5** | Over 2.5 | 1 - 0, 1 - 1 | Pending | `true` (show button) |
| **Under 2.5** | Under 2.5 | 2 - 1, 3 - 0 | Already Lost | `false` (hide button) |
| **Under 2.5** | Under 2.5 | 1 - 0, 0 - 0 | Pending | `true` (show button) |
| **StraightWin** | Home/Away Win | Any live score | In-play scores fluctuate | `true` (show button until match ends) |
| **Draw** | Draw | Any live score | In-play scores fluctuate | `true` (show button until match ends) |

### B. Razor View Updates

1. **`MatchPredictor.Web/Pages/Shared/_PredictionCard.cshtml`**:
   - Wrap `<button class="mp-add-cart-btn" ...>+ Select</button>` in `@if (MatchPredictor.Web.Helpers.PredictionDisplayHelper.CanAddToSelections(Model, nowUtc))`.
2. **`MatchPredictor.Web/Pages/Match/Index.cshtml`**:
   - Wrap the hero featured pick button in `@if (PredictionDisplayHelper.CanAddToSelections(p, nowUtc))`.
   - Wrap each multi-market breakdown card button in `@if (PredictionDisplayHelper.CanAddToSelections(pred, nowUtc))`.

### C. Client-Side Cart Drawer Guard (`cart.js`)

In `MatchPredictor.Web/wwwroot/js/cart.js`:
1. Check selection status during render:
   - Identify items where kickoff was > 200 minutes ago or where settled data is recorded.
   - Display a pill or subtitle `(Concluded / Settled)`.
2. In `buildBookingSelections(cart)`:
   - Exclude selections that are marked as concluded/settled or whose kickoff is past elapsed time.
   - Display a user-friendly toast if any expired selections were skipped during booking.

---

## 4. Affected Files

| File | Role | Action |
|---|---|---|
| `MatchPredictor.Application/Helpers/PredictionScoreClassHelper.cs` | Domain helper for live/settled score analysis | Add `HasLiveOptionDecided` method |
| `MatchPredictor.Web/Helpers/PredictionDisplayHelper.cs` | Presentation helper for UI checks | Add `CanAddToSelections` and `IsMatchEnded` |
| `MatchPredictor.Web/Pages/Shared/_PredictionCard.cshtml` | Prediction card partial | Condition button rendering on `CanAddToSelections` |
| `MatchPredictor.Web/Pages/Match/Index.cshtml` | Match detail page | Condition hero button and market card buttons on `CanAddToSelections` |
| `MatchPredictor.Web/wwwroot/js/cart.js` | Client cart management | Handle ended items in drawer & booking payload |
| `MatchPredictor.Tests.Unit/PredictionScoreClassHelperTests.cs` | Unit test suite | Add unit tests for `HasLiveOptionDecided` & `CanAddToSelections` |
| `MatchPredictor.Tests.Integration/CartJsTests.cs` | Integration tests for cart JS | Add tests for ended match handling in cart |

---

## 5. Task Breakdown

### Task 1: Core Domain & Helper Logic
- **Task ID:** `TASK-01`
- **Agent:** `backend-specialist`
- **Skills:** `clean-code`, `api-patterns`
- **Priority:** P0 (Blocker)
- **Dependencies:** None
- **INPUT:** `MatchPredictor.Application/Helpers/PredictionScoreClassHelper.cs`, `MatchPredictor.Web/Helpers/PredictionDisplayHelper.cs`
- **OUTPUT:**
  - Implement `PredictionScoreClassHelper.HasLiveOptionDecided(Prediction prediction)` with support for BTTS (won if both > 0, lost if no btts and both > 0), Over 2.5 (won if total > 2), and Under 2.5 (lost if total > 2).
  - Implement `PredictionDisplayHelper.IsMatchEnded(Prediction prediction, DateTime utcNow)`.
  - Implement `PredictionDisplayHelper.CanAddToSelections(Prediction prediction, DateTime utcNow)`.
- **VERIFY:** Unit tests verifying `CanAddToSelections` returns false for finished games, true for upcoming games, false for decided live games, and true for undecided live games.

### Task 2: Prediction Card & Match Detail Page Template Updates
- **Task ID:** `TASK-02`
- **Agent:** `frontend-specialist`
- **Skills:** `clean-code`, `frontend-design`
- **Priority:** P1
- **Dependencies:** `TASK-01`
- **INPUT:** `MatchPredictor.Web/Pages/Shared/_PredictionCard.cshtml`, `MatchPredictor.Web/Pages/Match/Index.cshtml`
- **OUTPUT:**
  - Wrap the `+ Select` button in `_PredictionCard.cshtml` in `@if (PredictionDisplayHelper.CanAddToSelections(Model, nowUtc))`.
  - Wrap the `+ Add to Selections` featured hero button in `Match/Index.cshtml` in `@if (PredictionDisplayHelper.CanAddToSelections(p, nowUtc))`.
  - Wrap each `+ Select` button in `Match/Index.cshtml` related predictions loop in `@if (PredictionDisplayHelper.CanAddToSelections(pred, nowUtc))`.
- **VERIFY:** Build and render pages: verify completed match cards do not contain the button element.

### Task 3: Selection Cart Drawer & Booking Guard
- **Task ID:** `TASK-03`
- **Agent:** `frontend-specialist`
- **Skills:** `clean-code`, `frontend-design`
- **Priority:** P1
- **Dependencies:** `TASK-01`
- **INPUT:** `MatchPredictor.Web/wwwroot/js/cart.js`
- **OUTPUT:**
  - In `cart.js`, check kickoff timestamps / settled flags when rendering cart items.
  - When building selections for booking, filter out any matches that have already ended.
  - If any selections are excluded, show an informative toast (e.g. `"X ended selections excluded from booking"`).
- **VERIFY:** Run `CartJsTests.cs` using Jint engine to verify cart filtering behavior.

### Task 4: Automated Testing Suite
- **Task ID:** `TASK-04`
- **Agent:** `test-engineer`
- **Skills:** `testing-patterns`, `clean-code`
- **Priority:** P1
- **Dependencies:** `TASK-01`, `TASK-02`, `TASK-03`
- **INPUT:** `MatchPredictor.Tests.Unit`, `MatchPredictor.Tests.Integration`
- **OUTPUT:**
  - Create/expand unit tests in `MatchPredictor.Tests.Unit/PredictionSelectionAvailabilityTests.cs`.
  - Expand `CartJsTests.cs` to test excluding concluded legs during booking.
  - Add Razor rendering integration test verifying button absence on completed matches.
- **VERIFY:** `DOTNET_CLI_HOME=/tmp dotnet test MatchPredictor.Tests.Unit` and `DOTNET_CLI_HOME=/tmp dotnet test MatchPredictor.Tests.Integration` pass with 100% green tests.

---

## 6. Phase X: Final Verification
 
- [x] **Unit Tests:** `dotnet test MatchPredictor.Tests.Unit` passes (291/291 passed).
- [x] **Integration Tests:** `dotnet test MatchPredictor.Tests.Integration` passes (PredictionSelectionAvailabilityTests: 9/9 passed, CartJsTests: 6/6 passed).
- [x] **Build Validation:** `dotnet build MatchPredictor.sln` succeeds with 0 errors.
- [x] **Manual UI Logic Verification:**
  - Prediction cards on `/predictions/*`: `CanAddToSelections` verified.
  - Match details page `/match/{id}`: Featured pick CTA and multi-market cards verified.
  - Cart drawer: Concluded/ended items tagged with pill and excluded from booking.
 
## ✅ PHASE X COMPLETE
- Build: ✅ Success (0 Errors)
- Unit Tests: ✅ Pass (291/291)
- Integration Tests: ✅ Pass (15/15)
- Date: October 2, 2026
