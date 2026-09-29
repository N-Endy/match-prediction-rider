# Plan: Fix AI Chat Match Card Rendering, Booking Action, and Natural Language Parsing

- **Slug:** `fix-ai-chat-matches-and-booking`
- **Primary Agent:** `project-planner`
- **Collaborating Agents:** `frontend-specialist`, `backend-specialist`, `test-engineer`
- **Target Files:**
  - `MatchPredictor.Web/Pages/AiChat.cshtml`
  - `MatchPredictor.Domain/Models/AiChatStreamChunk.cs`
  - `MatchPredictor.Infrastructure/Services/AiAdvisorService.cs`
  - `MatchPredictor.Infrastructure/Services/AiChatRequestParser.cs`
  - `MatchPredictor.Infrastructure/Services/AiChatContextBuilder.cs`
  - `MatchPredictor.Web/Api/AiChatController.cs`
  - `MatchPredictor.Tests.Integration/AiChatPageScriptTests.cs`
  - `MatchPredictor.Tests.Integration/AiChatControllerTests.cs`
  - `MatchPredictor.Tests.Integration/AiChatRequestParserTests.cs`
  - `MatchPredictor.Tests.Integration/AiAdvisorServiceTests.cs`
- **Status:** DRAFTED & READY FOR REVIEW

---

## 1. Root Cause Analysis

### Issue 1: "Before, the AI will bring out the list of matches with those little explanation texts. Now, it just tells what it will do without showing the matches."
1. **Missing Frontend Functions in Streaming Mode:**
   - In `MatchPredictor.Web/Pages/AiChat.cshtml` (lines 596 and 599), the SSE stream reader calls:
     ```javascript
     if (accumulatedCards.length > 0) {
         bubble.appendChild(buildKnowledgeCardList(accumulatedCards)); // ReferenceError!
     }
     if (accumulatedActions.length > 0) {
         bubble.appendChild(buildActionGrid(accumulatedActions)); // ReferenceError!
     }
     ```
   - Neither `buildActionGrid` nor `buildKnowledgeCardList` exists anywhere in the codebase. The real implementations are `buildKnowledgeCards(cards)` and `mp-chat-action-list` with `buildActionCard(action)`.
   - Because these calls are inside a `try ... catch (parseErr)` block intended to ignore malformed JSON lines, the JavaScript runtime throws a `ReferenceError` when the `metadata` event arrives, which is silently swallowed. Consequently, **no match action cards are ever appended to the DOM**.
2. **Casing Discrepancy between SSE Stream and Client:**
   - In `AiChatController.cs` (line 116), `Stream` serializes chunks with default `System.Text.Json.JsonSerializer.Serialize(chunk)`, producing PascalCase properties (`Action.HomeTeam`, `Action.AwayTeam`, `Action.Market`, etc.).
   - In `AiChat.cshtml`, `buildActionCard(action)` accesses camelCase properties (`action.homeTeam`, `action.awayTeam`, `action.market`). Without property normalization, all card titles and metadata evaluate to `undefined`.

### Issue 2: "It also doesn't book when I tell it to book the matches."
1. **Streaming Metadata Omission:**
   - `AiChatStreamChunk.cs` only has 8 properties and completely omits `ShowBookAll` and `AutoBook`.
   - Even when `AiAdvisorService` sets `AutoBook = true` and `ShowBookAll = true`, these flags are dropped during streaming.
   - In `AiChat.cshtml`'s stream handler (`sendChatMessage`), there is no code to render the "Add All & Open Slip" button (`mp-book-btn`) or trigger `addAllAndBook`.
2. **Booking Follow-Up Misrouting:**
   - In `AiAdvisorService.cs` (`IsBookingFollowUp`), follow-up booking requires `sessionState.LastRecommendedActionKeys.Count > 0`. If `sessionState.WorkingSlipActionKeys` has items but `LastRecommendedActionKeys` is empty, it fails to recognize the booking follow-up.
   - When a user asks "book the matches" or "book them", if the intent parser misclassifies the prompt as `MatchDiscussion`, it bypasses booking follow-up and fails.

### Issue 3: "Finally I don't think it understands natural english, and sometimes look for the text I wrote as matches"
1. **Aggressive `IsMatchDiscussionPrompt` Keyword Matching:**
   - In `AiChatContextBuilder.cs` (line 863):
     ```csharp
     (hasWorkingSlip && (prompt.Contains("these matches", StringComparison.Ordinal) || prompt.Contains("them", StringComparison.Ordinal)))
     ```
   - Any prompt containing the word `"them"` (e.g. `"book them"`, `"give me 5 of them"`, `"stake on them"`, `"what about them"`) when a slip or recommended set is active is classified as `MatchDiscussion`.
2. **Blind Entity Term Extraction:**
   - When intent is `MatchDiscussion`, `AiChatRequestParser.ExtractEntityTerms` extracts any token not found in the rigid `GenericPromptTokens` list as a team/fixture name.
   - Everyday conversational English words such as `"solid"`, `"plays"`, `"tonight"`, `"tomorrow"`, `"coupon"`, `"betslip"`, `"winnable"`, `"thinking"`, `"selections"` are not in `GenericPromptTokens`.
   - Consequently, the parser treats these words as football team names and executes a filter in `BuildSelection`. When no team matches the word (e.g. team `"tonight"` or team `"book"`), `NoRelevantMatchesFound` is set to `true`, and the AI replies:
     *"I couldn't find a relevant published match matching '<word>' in the recent card window."*

---

## 2. Proposed Solution

### A. Fix Streaming DOM Rendering in `AiChat.cshtml`
1. **Replace Undefined Functions:**
   - Replace `buildActionGrid(accumulatedActions)` with standard action list rendering using `mp-chat-action-list` and `buildActionCard(action)`.
   - Replace `buildKnowledgeCardList(accumulatedCards)` with `buildKnowledgeCards(accumulatedCards)`.
   - Provide backward-compatible safety aliases `buildActionGrid` and `buildKnowledgeCardList`.
2. **Normalize Action Object Casing:**
   - Add an action normalizer in `AiChat.cshtml` that handles both camelCase and PascalCase (`action.homeTeam || action.HomeTeam`).
3. **Render Warnings, Working Slip Summary, Book All Button & Auto-Book in Stream Handler:**
   - On the `metadata` chunk:
     - Render `response.warnings` (`mp-chat-warnings`).
     - Render `response.workingSlipSummary` (`buildWorkingSlipSummary`).
     - Render `showBookAll` footer with `mp-book-btn` ("Add All & Open Slip").
     - Trigger `autoBook` via `addAllAndBook(bookBtn)` when `chunk.autoBook` or `chunk.AutoBook` is true.

### B. Add `ShowBookAll` and `AutoBook` to `AiChatStreamChunk` & Server-Side Streaming
1. In `MatchPredictor.Domain/Models/AiChatStreamChunk.cs`:
   - Add `public bool ShowBookAll { get; set; }`
   - Add `public bool AutoBook { get; set; }`
2. In `MatchPredictor.Infrastructure/Services/AiAdvisorService.cs`:
   - In `StreamAdviceAsync`, pass `ShowBookAll = fullResponse.ShowBookAll` and `AutoBook = fullResponse.AutoBook` in the `metadata` chunk.
3. In `MatchPredictor.Web/Api/AiChatController.cs`:
   - In `Stream`, serialize chunks using camelCase naming policy or ensure compatible JSON naming.

### C. Refine Natural Language Parsing & Booking Follow-Up
1. **Prevent Booking Intent from becoming `MatchDiscussion`:**
   - In `AiChatContextBuilder.IsMatchDiscussionPrompt`:
     - If `MentionsBookingIntent(prompt)` is true, return `false`.
     - Remove overly broad `prompt.Contains("them")` unless accompanied by explicit discussion verbs (`"tell me about"`, `"explain"`, `"discuss"`).
2. **Expand `GenericPromptTokens`:**
   - In `AiChatContextBuilder.cs`, add missing conversational betting and temporal tokens:
     `"tonight"`, `"tomorrow"`, `"weekend"`, `"morning"`, `"afternoon"`, `"evening"`, `"night"`, `"later"`, `"now"`, `"soon"`, `"play"`, `"plays"`, `"stake"`, `"staked"`, `"betting"`, `"punter"`, `"coupon"`, `"ticket"`, `"slate"`, `"solid"`, `"winnable"`, `"sure"`, `"accurate"`, `"lock"`, `"bank"`, `"high"`, `"low"`, `"higher"`, `"lower"`, `"medium"`, `"look"`, `"looking"`, `"check"`, `"see"`, `"view"`, `"get"`, `"bring"`, `"pull"`, `"fetch"`, `"who"`, `"when"`, `"where"`, `"how"`, `"think"`, `"believe"`, `"expect"`, `"predict"`, `"predicted"`, `"tell"`, `"say"`, `"mention"`, `"provide"`, `"generate"`, `"create"`, `"build"`, `"selections"`, `"selection"`, `"against"`, `"versus"`, `"vs"`, `"at"`, `"favorite"`, `"favourite"`, `"underdog"`, `"choice"`, `"choices"`, `"score"`, `"scores"`, `"scoring"`, `"goal"`, `"goals"`, `"premier"`, `"league"`, `"leagues"`, `"cup"`.
3. **Support Booking Follow-Up with Working Slip and Context:**
   - In `AiAdvisorService.IsBookingFollowUp`, check `sessionState.LastRecommendedActionKeys.Count > 0 || sessionState.WorkingSlipActionKeys.Count > 0`.
   - If the user asks to "book the matches", "book them", or "add them", resolve the action keys from the last recommended or working slip set and immediately return the booking follow-up response.

---

## 3. Step-by-Step Task Breakdown

### Phase 1: Models & Server Streaming Pipeline
- [x] Task 1.1: In `MatchPredictor.Domain/Models/AiChatStreamChunk.cs`, add `ShowBookAll` and `AutoBook` properties.
- [x] Task 1.2: In `MatchPredictor.Infrastructure/Services/AiAdvisorService.cs` (`StreamAdviceAsync`), populate `ShowBookAll` and `AutoBook` in the `metadata` chunk.
- [x] Task 1.3: In `MatchPredictor.Web/Api/AiChatController.cs`, ensure streaming JSON serialization includes `ShowBookAll` and `AutoBook` with camelCase support.

### Phase 2: Frontend Streaming & Match Card Rendering (`AiChat.cshtml`)
- [x] Task 2.1: In `MatchPredictor.Web/Pages/AiChat.cshtml`, add `normalizeAction(action)` to handle both camelCase and PascalCase properties safely.
- [x] Task 2.2: In `sendChatMessage()` stream reader, replace `buildActionGrid(accumulatedActions)` with standard `mp-chat-action-list` containing `buildActionCard(action)`.
- [x] Task 2.3: In `sendChatMessage()` stream reader, replace `buildKnowledgeCardList(accumulatedCards)` with `buildKnowledgeCards(accumulatedCards)`.
- [x] Task 2.4: In `sendChatMessage()`, handle `metadata` chunk by rendering `warnings`, `workingSlipSummary`, and the "Add All & Open Slip" button (`mp-book-btn`) when `showBookAll` is true.
- [x] Task 2.5: In `sendChatMessage()`, execute `autoBook` logic when `autoBook` is true so matches are automatically added to the cart drawer/slip.
- [x] Task 2.6: Define `buildActionGrid` and `buildKnowledgeCardList` aliases in JavaScript as defensive fallbacks.

### Phase 3: Natural Language Parsing & Booking Follow-Up Hardening
- [x] Task 3.1: In `MatchPredictor.Infrastructure/Services/AiChatContextBuilder.cs`, expand `GenericPromptTokens` with conversational words, temporal keywords, and common betting terminology.
- [x] Task 3.2: In `AiChatContextBuilder.IsMatchDiscussionPrompt`, ensure prompts with booking intent (`MentionsBookingIntent`) never evaluate to match discussion, and narrow the `them` check.
- [x] Task 3.3: In `AiAdvisorService.cs` (`IsBookingFollowUp`), allow booking follow-up when either `LastRecommendedActionKeys` or `WorkingSlipActionKeys` are present, and recognize phrases like `"book the matches"`.
- [x] Task 3.4: In `AiChatRequestParser.cs` (`ExtractEntityTerms`), ensure entity terms are sanitized and cleared for conversational requests that do not target specific fixtures.

### Phase 4: Integration Testing & Verification
- [x] Task 4.1: In `AiChatPageScriptTests.cs`, add tests verifying streaming reader handles `action` cards, `showBookAll`, `autoBook`, `buildActionCard`, and does not reference undefined functions.
- [x] Task 4.2: In `AiChatControllerTests.cs`, verify that streaming emits `ShowBookAll` and `AutoBook` in metadata.
- [x] Task 4.3: In `AiChatRequestParserTests.cs`, add tests verifying natural English phrases (e.g. `"book the matches"`, `"get me solid plays for tonight"`) do not produce invalid entity terms or misclassified intents.
- [x] Task 4.4: In `AiAdvisorServiceTests.cs`, add tests verifying booking follow-up works when asking `"book the matches"` after receiving recommendations.
- [x] Task 4.5: Run full test suites: unit tests and integration tests.

---

## 4. Verification Criteria
1. Asking for predictions (e.g. *"Give me 10 of the best BTTS predictions"* or *"Give me a mixture of btts, over 2.5 and straight win"*) displays the AI explanation text **AND renders the individual match action cards with odds, model edge, and "+ Add" buttons below the bubble**.
2. An "Add All & Open Slip" button appears below the match cards when multiple picks are recommended.
3. Saying *"book the matches"* or *"book them"* automatically books the picks, adds them to the betslip cart, and opens the slip.
4. Natural English prompts containing words like *"tonight"*, *"solid"*, *"plays"*, *"the matches"* are properly understood and never searched for as literal football team names.
5. All automated unit and integration tests pass cleanly.
