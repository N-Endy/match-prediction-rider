# Implementation Plan: AI Chat UI/UX Redesign & Modernization

**Plan File:** `docs/PLAN-aichat-ui-ux.md`  
**Target Surface:** `MatchPredictor.Web/Pages/AiChat.cshtml` & `site.css`  
**Status:** COMPLETED - Verified by Automated & Integration Tests  

---

## 1. Executive Summary & UX Audit Findings

A comprehensive review of the current AI Betting Companion page (`/AiChat`) against modern UI/UX design laws, cognitive ergonomics, and accessibility standards revealed key design bottlenecks:

### 🚨 Current Issues & UX Anti-Patterns
1. **Viewport & Nested Scrollbar Defect (Ergonomics):**
   - The chat container uses a hardcoded `height: calc(100vh - 200px)` inside the regular page layout. This creates a "double scrollbar" trap where both the browser window and the message list scroll independently. On mobile devices, `100vh` ignores browser address bars, causing the input composer to be pushed off-screen.
2. **Single-Line Input Constraint (Fitts's Law & Cognitive Ergonomics):**
   - The message input uses a single-line `<input type="text">`. Users cannot input multi-line questions, paste match lists, or use `Shift+Enter` for line breaks.
3. **Empty State & Suggestion Architecture (Hick's Law):**
   - The initial onboarding presents a single long text bubble with 6 plain horizontal chip buttons. There is no clear visual taxonomy (e.g. Accumulator Builder vs. Value Bet Hunting vs. Model Explanations).
4. **Platform-Dependent Emoji Avatars (Visual Identity & Professionalism):**
   - Avatars use raw OS emojis (`🤖` and `👤`), which render inconsistently across Windows, macOS, iOS, and Android.
5. **Dense Action & Match Cards (Miller's Law / Chunking):**
   - Match actions join critical statistics with plain text bullets: `"Today • Card • Market • Pick • 18:30 • ~1.85 odds"`. There is no visual hierarchy for probabilities, edge indicators, or league badges.
6. **Lack of Session Controls & Utility (User Control & Freedom):**
   - No "New Chat / Clear Conversation" button.
   - No 1-click "Copy message" button on AI answers.
   - No "Scroll to bottom" button when reviewing previous responses.
7. **Accessibility & ARIA Compliance:**
   - Message streaming lacks `aria-live="polite"` live regions.
   - Composer inputs and action buttons lack explicit `aria-label` tags.

---

## 2. Design Vision & Target Experience

Transform the AI Chat into a **cutting-edge, dark-mode conversational cockpit** adhering to MatchPredictor's aesthetic tokens:
- **Zero Purple/Violet Policy:** Cyan, Emerald, Slate, and Sky Blue accent palette.
- **True Viewport Fit:** Clean application layout using modern `100dvh` with sticky header, scrollable message stream, and floating input dock.
- **Rich Interactive Match Cards:** Dedicated cards with team badges, probability comparison bars, positive edge chips, and 1-click "Add to Betslip" buttons with animated checkmark feedback.
- **Auto-Expanding Composer:** Multi-line textarea that expands up to 5 lines with `Enter` (send) and `Shift+Enter` (new line) shortcuts.

---

## 3. Step-by-Step Implementation Breakdown

### Phase 1: Layout, Viewport & Container Architecture
- [x] **Task 1.1: Full-Height Clean Viewport Layout**
  - Implement full-height viewport styling (`height: calc(100dvh - var(--navbar-height))`) eliminating the double scrollbar bug.
  - Implement mobile keyboard safety (using CSS visual viewport units and flex containers).
- [x] **Task 1.2: Chat Header & Session Utility Bar**
  - Add chat top-bar with active status pill ("Online · Grounded with Live Matches"), model badge, and session controls.
  - Add "New Conversation" / "Clear Chat" button with confirmation tooltip.
  - Keep authenticated logout button compact and accessible.

### Phase 2: Onboarding & Categorized Prompt Architecture
- [x] **Task 2.1: Modern Welcome Hero & Feature Cards**
  - Replace the generic greeting with a sleek AI companion card introducing Nelson.
  - Group suggested prompts into 3 distinct functional buckets:
    1. **Slip Builder:** "Build a 5-leg weekend accumulator", "Give me 10 top BTTS picks"
    2. **Value & Edge Finder:** "Where is the highest model edge today?", "Which draw games have value?"
    3. **Strategy & Model Deep Dive:** "Explain Poisson vs Dixon-Coles", "Why was this pick suppressed?"
- [x] **Task 2.2: Interactive Quick-Action Chips**
  - Redesign prompt chips with subtle hover states, icons, and keyboard focus outlines meeting 44px touch target guidelines.

### Phase 3: Input Composer & Multiline Ergonomics
- [x] **Task 3.1: Auto-Expanding Textarea Composer**
  - Replace `<input type="text">` with auto-resizing `<textarea>` (min-height 48px, max-height 160px).
  - Support `Enter` to submit and `Shift+Enter` for newlines.
- [x] **Task 3.2: Modern Send & Action Toolbar**
  - Modernize the send button with SVG arrow/plane icon and smooth active states.
  - Display loading/streaming spinner inside the button while waiting for a response.
  - Add an input helper micro-text: `"Press Enter to send, Shift+Enter for new line"`.

### Phase 4: Message Stream, Avatars & Enhanced Markdown
- [x] **Task 4.1: Bespoke SVG Avatars & Clean Bubbles**
  - Replace raw emojis with sleek SVG robot and user avatar tokens.
  - Refine bubble styling with proper padding, border-radius, and readable typography (line-height 1.6, max line length 70ch).
- [x] **Task 4.2: Markdown & Code Rendering Enhancements**
  - Upgrade `formatMessageHtml` to support:
    - Bulleted & numbered lists (`*`, `-`, `1.`)
    - Headings (`###`)
    - Code blocks and inline backticks
    - Tables / structured text
- [x] **Task 4.3: Message Action Utilities**
  - Add a 1-click "Copy Answer" button below AI messages with "Copied!" visual feedback.
  - Add a floating "Scroll to bottom" button that appears when the user scrolls up.

### Phase 5: Structured Match & Knowledge Card Redesign
- [x] **Task 5.1: Football Match Action Cards (`.mp-chat-action`)**
  - Restructure match cards:
    - Home vs Away teams in bold high-contrast typography.
    - League badge and kickoff time pill.
    - Target market & pick badge (color-coded by market type: cyan for over/under, emerald for win, amber for BTTS).
    - Statistical meter comparing Model Probability vs Market Probability.
    - Positive edge indicator pill (e.g. `+4.5% Edge`).
- [x] **Task 5.2: "Add to Betslip" Micro-Interactions**
  - Update "+ Add [Market]" button to provide instant animated feedback:
    - On click: transforms into `✓ Added` with emerald glow before opening or docking the slip.
- [x] **Task 5.3: Football Read Insight Panel (`.mp-chat-insight`)**
  - Clean up the tactical rationale section with clear bullet hierarchy and source tags.

### Phase 6: Streaming Feedback & Accessibility (a11y)
- [x] **Task 6.1: Smooth Streaming & Thought Indicator**
  - Enhance the thinking indicator with subtle glowing cyan pulse and real-time step descriptions.
- [x] **Task 6.2: WCAG 2.1 AA Compliance & Keyboard Navigation**
  - Add `aria-live="polite"` and `role="log"` to the message stream container.
  - Add explicit `aria-label` attributes to the composer, send button, and action controls.
  - Verify high contrast ratios for text against dark backgrounds.

### Phase 7: Verification & Automated Audits
- [x] **Task 7.1: Automated UX & Accessibility Audits**
  - Run `python .agent/skills/frontend-design/scripts/ux_audit.py MatchPredictor.Web/Pages/AiChat.cshtml`.
  - Verify 0 critical accessibility failures.
- [x] **Task 7.2: Cross-Device & Responsive Verification**
  - Verify layout responsiveness on mobile (375px/390px), tablet (768px), and desktop (1440px).
  - Verify end-to-end streaming chat, prompt chips, and betslip cart integration.

---

## 4. Architectural File Map

| Component | Target File | Nature of Changes |
| :--- | :--- | :--- |
| **View Template & Scripts** | `MatchPredictor.Web/Pages/AiChat.cshtml` | Full layout refactoring, multiline textarea, SVG avatars, markdown engine, card structure |
| **Chat Design System CSS** | `MatchPredictor.Web/wwwroot/css/site.css` | Viewport flex container (`100dvh`), composer dock, match card tokens, pulse animations |
| **Integration Tests** | `MatchPredictor.Tests.Integration/AiChatPageScriptTests.cs` | Verify markup elements, script functions, and event listeners |

---

## 5. Agent Assignments

- **Lead Agent:** `frontend-specialist` (UI architecture, CSS design system, responsive layouts, micro-interactions)
- **Reviewer / Planner:** `project-planner` (milestones, UX heuristics compliance, task breakdown)
- **Quality Assurance:** `qa-automation-engineer` (ux_audit.py, accessibility validation, browser rendering)
