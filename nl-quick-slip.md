# Natural Language Quick Slip & Grounded Match Co-Pilot

> **Full Detailed Plan:** See [docs/PLAN-nl-quick-slip.md](file:///Users/nnamdi/Desktop/Okafor%20Nelson/Projects/MatchPredictor/MatchPredictor/docs/PLAN-nl-quick-slip.md)

- **Slug:** `nl-quick-slip`
- **Primary Agent:** `project-planner`
- **Collaborating Agents:** `backend-specialist`, `frontend-specialist`, `security-auditor`, `test-engineer`
- **Target Solution:** `MatchPredictor.sln` (.NET 10 / ASP.NET Core Razor Pages / EF Core / PostgreSQL / Vanilla JS)
- **Status:** COMPLETED & VERIFIED ✅

---

## Executive Summary

Adapted the high-performance natural language input and zero-hallucination fact grounding patterns from **FinanceApp** into **MatchPredictor**:
1. **Quick Slip Bar:** Ambient natural language input bar on `/Index`, `/Betslips`, and the Betslip drawer.
2. **Two-Tier Parser:** Fast local regex/token parser (`QuickSlipParser`, <2ms) with LLM JSON schema fallback (`QuickSlipService`).
3. **Interactive Staged Slip Card:** Preview calculated combined odds, model edge, and 1-click "Add to Slip" or "Replace Slip".
4. **`FixtureFacts` Anti-Hallucination Layer:** Serializes real database parameters (Dixon-Coles, Poisson, Elo, verified bookmaker odds) and restricts AI generation to verified facts only.
