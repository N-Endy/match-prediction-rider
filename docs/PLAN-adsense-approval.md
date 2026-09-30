# Implementation Plan: Google AdSense "Low Value Content" Resolution & Approval

**Target File:** `docs/PLAN-adsense-approval.md`  
**Project Type:** WEB (ASP.NET Core Razor Pages / .NET 10)  
**Primary Agent:** `project-planner`  
**Supporting Agents:** `frontend-specialist`, `backend-specialist`, `seo-specialist`, `security-auditor`  
**Key Skills:** `seo-fundamentals`, `frontend-design`, `clean-code`, `api-patterns`  

---

## 1. Overview & Root Cause Analysis

Google AdSense rejected the site with the **"Low value content"** policy notice:
> *"To qualify for ad serving, a site must provide substantial unique value, establish a consistent presence on the web, and show a level of user interest that supports a commercial advertising partnership. Before re-submitting your site, ensure that it: Provides authentic, high-quality information, tools, or services; Exhibits ongoing curation and structural maintenance; Generates and sustains genuine user interest."*

### Why MatchPredictor Was Flagged (The 5 Pillars)

1. **Thin Programmatic Content & Low Indexable Page Count:**
   - The site currently only exposes ~14 static URLs in `sitemap.xml`.
   - Predictions are only presented as card listings inside 5 generic category pages (`/predictions/btts`, `/predictions/over2`, etc.).
   - There are **no dedicated match preview pages** (e.g. `/match/{slug}` or `/match/{fixtureId}`) providing in-depth analysis, head-to-head statistics, probability distributions, team form charts, and editorial rationale. To automated review bots, the site looks like a thin programmatic data scraper rather than a substantial content publication.
2. **Absence of Rich Educational & Editorial Content:**
   - AdSense requires human-curated, authoritative editorial content. The site lacks a dedicated **Articles / Knowledge Base / Match Analysis Blog** (e.g., weekly league previews, guides on mathematical football modeling like Dixon-Coles, Shin margin removal, Brier score calibration, and bankroll management).
3. **Gambling / Tipster Language Red Flags:**
   - Several UI elements use aggressive betting terminology (e.g., `<button class="mp-add-cart-btn">Add Bet</button>`, "Betslips", slip-export booking codes).
   - Google AdSense has strict policies regarding gambling-adjacent websites. If a site is perceived as an uncurated "betting tipster" rather than a legitimate sports statistical research and predictive modeling tool, it is immediately denied.
4. **Thin E-E-A-T (Experience, Expertise, Authoritativeness, Trustworthiness):**
   - No named author/analyst profiles or editorial team credentials explaining *who* builds and reviews the mathematical models.
   - Minimal responsible gambling trust badges (e.g., BeGambleAware, GamCare, 18+ verification) and physical/entity contact clarity.
5. **Organic Search Traffic & "Sustained User Interest":**
   - The rejection explicitly highlights: *"show a level of user interest that supports a commercial advertising partnership"*.
   - If the site is brand new with fewer than 50–100 daily organic search visits, AdSense’s automated gating rejects applications until organic search impressions and indexation are established via Google Search Console.

---

## 2. Success Criteria

- [ ] **Expanded Content Footprint:** Dynamic individual match preview pages (`/match/{slug}`) with comprehensive match statistics, head-to-head data, probability breakdown, and editorial summary (expanding sitemap from 14 to 100+ indexable pages).
- [ ] **Educational & Editorial Hub:** A dedicated `/articles` or `/guides` section with at least 5–8 high-quality, long-form evergreen articles explaining predictive modeling, Poisson distribution, football analytics, and league betting trends.
- [ ] **Compliance & Policy Alignment:** Refactor UI terminology away from gambling cues (replace "Add Bet" with "Save Pick" / "Add to Watchlist", reframe "Betslips" to "Curated Selection Lists" or "Model Portfolios"), with prominent 18+ & Responsible Analytics notices.
- [ ] **E-E-A-T & Trust Signals:** Detailed About/Editorial team page with verified author personas, data source disclosures, contact details, and structured Schema.org markup (`SportsEvent`, `AnalysisNewsArticle`, `Organization`, `FAQPage`).
- [ ] **SEO & Indexation Health:** Clean XML sitemap generation for dynamic match pages, robots.txt optimization, Google Search Console indexing verification, and Core Web Vitals compliance.
- [ ] **Traffic Readiness Gate:** Minimum 30 days of consistent publishing history and active search impression traction prior to final AdSense re-submission.

---

## 3. Architecture & Tech Stack Changes

- **Framework:** ASP.NET Core (.NET 10) Razor Pages
- **Database:** PostgreSQL with EF Core (`MatchPredictor.Infrastructure`)
- **New Entities / View Models:**
  - `MatchDetailViewModel`: Head-to-head records, recent form, probability distribution breakdown, team stats.
  - `Article` / `GuideModel`: Lightweight Markdown-backed or DB-backed educational guides.
- **Routing:**
  - `/match/{slug}` or `/match/{id}/{slug}`: Canonical match preview and statistical analysis.
  - `/guides`: Educational library for predictive football modeling.
  - `/guides/{slug}`: Long-form analytical articles.
- **Structured Data:** JSON-LD schema injection for `SportsEvent`, `FAQPage`, and `Article`.

---

## 4. Proposed File Structure Additions

```
MatchPredictor.Web/
├── Pages/
│   ├── Match/
│   │   ├── Index.cshtml            # /match/{id}/{slug} - Full match preview & stats
│   │   └── Index.cshtml.cs
│   ├── Guides/
│   │   ├── Index.cshtml            # /guides - Educational articles & modeling insights
│   │   ├── Index.cshtml.cs
│   │   ├── Details.cshtml          # /guides/{slug} - Deep dive article
│   │   └── Details.cshtml.cs
│   ├── Shared/
│   │   ├── _MatchStatsDeepDive.cshtml
│   │   ├── _AuthorBioCard.cshtml
│   │   ├── _ResponsibleGamblingBanner.cshtml
│   │   └── _SchemaJsonLd.cshtml
│   └── About.cshtml                # Enhanced with Team & Editorial board E-E-A-T
└── wwwroot/
    ├── content/
    │   └── guides/                 # High quality markdown/static guides
    │       ├── understanding-expected-goals-xg.md
    │       ├── dixon-coles-and-poisson-modeling.md
    │       ├── value-investing-in-football-markets.md
    │       ├── brier-score-and-model-calibration.md
    │       └── bankroll-management-for-sports-traders.md
    └── sitemap.xml                 # Dynamic / expanded sitemap generator
```

---

## 5. Task Breakdown

### Phase 1: Terminology & Policy Compliance Cleanup (Immediate)
*Goal: Remove gambling/tipster red flags that trigger instant AdSense policy disqualification.*

- **Task 1.1: Rebrand UI Terminology & CTA Actions**
  - **Agent:** `frontend-specialist`
  - **Skills:** `clean-code`, `frontend-design`
  - **Priority:** P0
  - **Dependencies:** None
  - **INPUT:** `_PredictionCard.cshtml`, `Betslips.cshtml`, `Index.cshtml`
  - **OUTPUT:** Change button text "Add Bet" → "Save Pick" or "Track Selection"; rephrase "Betslips" to "Curated Selection Lists" or "Model Portfolios"; remove raw sportsbook affiliate deep-linking triggers that appear aggressive.
  - **VERIFY:** Search codebase for "Add Bet" and betting promotion text; confirm UI renders neutral analytics terms.

- **Task 1.2: Responsible Analytics & Trust Footer**
  - **Agent:** `frontend-specialist`
  - **Skills:** `frontend-design`
  - **Priority:** P0
  - **Dependencies:** None
  - **INPUT:** `_Layout.cshtml`, `Privacy.cshtml`, `About.cshtml`
  - **OUTPUT:** Add a persistent footer badge: "18+ Only | Informational Sports Analytics Tool | Not a Bookmaker | Please Gamble Responsibly (BeGambleAware.org / GamCare)".
  - **VERIFY:** Inspect rendered layout on all pages.

---

### Phase 2: High-Value Individual Match Pages (`/match/{id}/{slug}`)
*Goal: Eliminate "thin content" by providing unique, in-depth statistical previews for each fixture.*

- **Task 2.1: Match Detail Page Model & Endpoint**
  - **Agent:** `backend-specialist`
  - **Skills:** `api-patterns`, `clean-code`
  - **Priority:** P1
  - **Dependencies:** Task 1.1
  - **INPUT:** `Prediction`, `ForecastObservation`, historical fixture data in DB.
  - **OUTPUT:** New Razor page `MatchPredictor.Web/Pages/Match/Index.cshtml` mapped to `/match/{id:int}/{slug?}`.
  - **VERIFY:** Navigating to `/match/123/arsenal-vs-chelsea` loads fixture details, model probability breakdown, confidence rating, and form.

- **Task 2.2: Rich Visual Match Analysis Component**
  - **Agent:** `frontend-specialist`
  - **Skills:** `frontend-design`
  - **Priority:** P1
  - **Dependencies:** Task 2.1
  - **INPUT:** `_MatchStatsDeepDive.cshtml`
  - **OUTPUT:** Comprehensive match statistics panel:
    1. Model probability vs. market implied probability.
    2. Head-to-head recent meetings.
    3. Last 5 matches goal averages and clean-sheet trends.
    4. Model justification text explaining *why* the prediction was selected.
  - **VERIFY:** Page renders without console errors; mobile-responsive layout passes WCAG AA contrast.

- **Task 2.3: Link Prediction Cards to Match Detail Pages**
  - **Agent:** `frontend-specialist`
  - **Skills:** `clean-code`
  - **Priority:** P1
  - **Dependencies:** Task 2.2
  - **INPUT:** `_PredictionCard.cshtml`
  - **OUTPUT:** Match card titles become clickable links pointing to `/match/{id}/{slug}`.
  - **VERIFY:** Clicking any fixture card opens its dedicated deep-dive page.

---

### Phase 3: Authority Editorial & Educational Hub (`/guides`)
*Goal: Provide substantial, evergreen unique value that Google AdSense explicitly demands.*

- **Task 3.1: Educational Articles Section**
  - **Agent:** `frontend-specialist` & `backend-specialist`
  - **Skills:** `clean-code`, `seo-fundamentals`
  - **Priority:** P1
  - **Dependencies:** None
  - **INPUT:** Razor Pages for `/guides` and `/guides/{slug}`.
  - **OUTPUT:** An educational portal with 5 in-depth, original technical articles:
    1. *How Mathematical Modeling Predicts Football Matches (Dixon-Coles & Poisson)*
    2. *Probability Calibration & Brier Scores: Why Raw Bookmaker Odds Are Biased*
    3. *Understanding Both Teams to Score (BTTS) Dynamics in Modern Football*
    4. *Over/Under 2.5 Goals: Statistical Distribution and Pace Analysis*
    5. *Bankroll Discipline and Variance in Sports Forecasting*
  - **VERIFY:** All articles render cleanly with proper headings, diagrams/tables, author credits, and 1,200+ words of authentic content.

---

### Phase 4: E-E-A-T, Schema Markup & Technical SEO
*Goal: Prove authentic authorship, curation, and search indexability to Google reviewers.*

- **Task 4.1: Author Bio & Editorial Transparency Page**
  - **Agent:** `frontend-specialist`
  - **Skills:** `seo-fundamentals`
  - **Priority:** P1
  - **Dependencies:** None
  - **INPUT:** `About.cshtml`, `Contact.cshtml`
  - **OUTPUT:** Add detailed editorial team bios (Lead Data Modeler, Sports Analyst), editorial policy statement, revision history, and physical entity contact/support info.
  - **VERIFY:** Inspect `About.cshtml` and confirm transparent disclosures.

- **Task 4.2: Structured Data (Schema.org JSON-LD)**
  - **Agent:** `seo-specialist`
  - **Skills:** `seo-fundamentals`
  - **Priority:** P2
  - **Dependencies:** Tasks 2.1, 3.1
  - **INPUT:** `_Layout.cshtml`, `Match/Index.cshtml`, `Guides/Details.cshtml`
  - **OUTPUT:** JSON-LD schema for:
    - `SportsEvent` on match pages (teams, date, sport, location).
    - `Article` on guide pages (author, publisher, dates).
    - `Organization` & `WebSite` with SearchAction on homepage.
  - **VERIFY:** Validate schemas using Google Rich Results Test syntax.

- **Task 4.3: Dynamic XML Sitemap Expansion**
  - **Agent:** `backend-specialist`
  - **Skills:** `seo-fundamentals`
  - **Priority:** P2
  - **Dependencies:** Tasks 2.1, 3.1
  - **INPUT:** `sitemap.xml` / dynamic sitemap controller.
  - **OUTPUT:** Sitemap dynamically indexes all active match pages, past settled match archives, and all educational guides.
  - **VERIFY:** `curl https://matchpredictor.dev/sitemap.xml` returns valid XML with 50+ fresh URLs.

---

### Phase 5: Traffic & Re-Submission Verification (Phase X Gate)
*Goal: Ensure the site fulfills the "Generates and sustains genuine user interest" requirement before clicking 'Request Review'.*

- **Task 5.1: Google Search Console Indexing Audit**
  - Verify that Google has indexed at least 30+ pages from the new sitemap.
- **Task 5.2: Organic Traffic Baseline Check**
  - Confirm Google Analytics records steady organic visitor traffic (minimum 30–60 days of consistent activity).
- **Task 5.3: Automated Security & Quality Scans**
  - Run `.agent/scripts/verify_all.py .`
  - Run `security_scan.py .`
  - Run `ux_audit.py .`
- **Task 5.4: Formal AdSense Re-Submission**
  - Remove test ad slot placeholders or ensure fallback containers do not cause layout shifts.
  - Submit site review in Google AdSense dashboard.

---

## 6. Phase X: Final Verification Checklist

- [ ] UI contains zero prohibited aggressive betting copy ("Add Bet" replaced).
- [ ] Responsible gambling badges and 18+ warnings are visible sitewide.
- [ ] At least 5 long-form educational guides published and accessible from navigation.
- [ ] Individual `/match/{id}/{slug}` pages live and linked from prediction cards.
- [ ] Schema.org structured data passes Google Rich Results test with 0 errors.
- [ ] XML sitemap reflects all active pages and is submitted to Google Search Console.
- [ ] No empty fixture pages or 404 broken links exist.
- [ ] Python security and UX audit scripts pass cleanly.
