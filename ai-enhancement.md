# AI Engine Enhancement Plan: Deep Match Research, Enterprise Guardrails & Streaming Agent

> **Full Detailed Plan:** See [docs/PLAN-ai-enhancement.md](file:///Users/nnamdi/Desktop/Okafor%20Nelson/Projects/MatchPredictor/MatchPredictor/docs/PLAN-ai-enhancement.md)

- **Slug:** `ai-enhancement`
- **Primary Agent:** `project-planner`
- **Collaborating Agents:** `backend-specialist`, `security-auditor`, `frontend-specialist`, `test-engineer`, `qa-automation-engineer`
- **Target Solution:** `MatchPredictor.sln` (.NET 10 / ASP.NET Core Razor Pages / EF Core / PostgreSQL)
- **Status:** APPROVED & READY FOR ORCHESTRATION

---

## Plan Overview

1. **Enterprise Information Protection & Multi-Layer Guardrails:**
   - Layer 1: Inbound deterministic regex & heuristic pre-filter blocking prompt injections, jailbreaks, and secret probes.
   - Layer 2: Hardened system prompt grounding defining strict persona and boundary rules.
   - Layer 3: Outbound response sanitizer scanning and redacting any potential credential or algorithm leakage.
   - Safe analytics explainer for user-facing metrics (Brier, Calibrator Eras, Value Bets, Thresholds).

2. **Extensive Deep Match Research Engine:**
   - Synthesis of internal statistical models (calibrated probabilities, Dixon-Coles, Elo, historical backtest outcomes).
   - Real-time external aggregation of confirmed/predicted lineups, player injuries, home/away venue splits, H2H dynamics, and odds line movements.
   - Strict 6-second timeout allowance ensuring comprehensive data collection before synthesizing the match dossier.

3. **Tool-Calling Agent & Real-Time Streaming:**
   - Server-Sent Events (SSE) `/api/ai/chat/stream` for real-time typewriter effect.
   - ReAct tool calling (`ResearchMatchup`, `LookupAppGuide`, `EvaluateValueEdge`, `ComposeOptimizedSlip`).
   - Tiered model routing: fast tier (Gemini Flash / Groq Llama 3) for quick Q&A; reasoning tier (Gemini Pro / GPT-5) for extensive match research dossiers.

---

## Phase Breakdown

- **Phase 1:** Security Guardrail & Output Sanitization Layer (`security-auditor` + `backend-specialist`)
- **Phase 2:** Deep Match Research Engine & Stats Aggregator (`backend-specialist`)
- **Phase 3:** Comprehensive App & Domain Knowledge Base (`backend-specialist` + `documentation-writer`)
- **Phase 4:** Streaming & Tool-Calling ReAct Engine (`backend-specialist` + `frontend-specialist`)
- **Phase 5:** Verification, Benchmarking & Penetration Testing (`qa-automation-engineer` + `security-auditor` + `test-engineer`)
