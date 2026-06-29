# Plan - Chrome Headless Scraper Timeout Fix

## Overview
This plan addresses the 120-second Selenium WebDriver timeout issues affecting the primary score scraper in `WebScraperService.cs`. The scraper hangs when executing `driver.FindElement(By.Id("score-data"))` because the underlying headless Chrome process either crashes due to low memory limits (`--js-flags=--max-old-space-size=128`) or hangs due to being blocked by Cloudflare's bot-detection challenge pages on `https://www.flashscore.mobi`.

We will implement a series of fixes:
1. Increase or remove the restrictive JavaScript memory limit.
2. Enable anti-detection configurations and a real User-Agent for the primary scraper session.
3. Configure explicit page-load and command timeouts.
4. Replace immediate synchronous element lookups with `WebDriverWait` and fail-fast checks for challenge pages.

## Project Type
**BACKEND** (C# background worker and Selenium scraping infrastructure)

## Success Criteria
- The primary score scraping task in [WebScraperService](file:///Users/nnamdi/Desktop/Projects/MatchPredictor/MatchPredictor/MatchPredictor.Infrastructure/WebScraperService.cs) does not hang for 120 seconds.
- Headless Chrome memory capacity is increased to prevent OOM renderer crashes.
- Proper anti-detection headers are sent by the primary scraper to reduce bot blocks on `https://www.flashscore.mobi`.
- If a challenge page or navigation failure occurs, the scraper fails fast (under 30s) and hands over to fallback stages.
- The project builds successfully, and integration tests pass.

## Tech Stack
- .NET / C#
- Selenium WebDriver & ChromeDriver (C# OpenQA.Selenium library)

## File Structure
Only one existing file will be modified:
- [WebScraperService.cs](file:///Users/nnamdi/Desktop/Projects/MatchPredictor/MatchPredictor/MatchPredictor.Infrastructure/WebScraperService.cs)

---

## Open Questions
> [!IMPORTANT]
> - **Memory constraints on deployment server**: Is the background worker deployed on a 512MB RAM instance? If so, increasing the Chrome max memory size to 512MB could trigger host-level OOMs if other processes are memory-intensive. We must ensure a balanced memory footprint.
> - **Alternative proxy or user agent**: Do we have proxy servers to rotate if FlashScore starts hard-blocking the current hosting IP address?

---

## Task Breakdown

### Task 1: Increase Headless Chrome JS heap memory limit
- **Agent**: `debugger`
- **Skill**: `clean-code`
- **Priority**: High (P0)
- **Dependencies**: None
- **Description**: Modify [GetChromeOptions](file:///Users/nnamdi/Desktop/Projects/MatchPredictor/MatchPredictor/MatchPredictor.Infrastructure/WebScraperService.cs#L2009-L2039) to increase `--js-flags=--max-old-space-size=128` to `--js-flags=--max-old-space-size=512`.
- **INPUT**: Current `GetChromeOptions` implementation.
- **OUTPUT**: Updated `GetChromeOptions` method with a 512MB heap limit.
- **VERIFY**: Compile code and ensure memory allocation flags are correctly passed.

### Task 2: Configure WebDriver PageLoad and Script timeouts
- **Agent**: `backend-specialist`
- **Skill**: `clean-code`
- **Priority**: High (P0)
- **Dependencies**: None
- **Description**: Update [RunWithChromeSessionAsync](file:///Users/nnamdi/Desktop/Projects/MatchPredictor/MatchPredictor/MatchPredictor.Infrastructure/WebScraperService.cs#L2130-L2189) to configure the newly instantiated driver with explicit timeouts:
  ```csharp
  driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(30);
  driver.Manage().Timeouts().AsynchronousJavaScript = TimeSpan.FromSeconds(30);
  ```
- **INPUT**: `RunWithChromeSessionAsync` driver setup code.
- **OUTPUT**: Timeout properties set on driver initialization.
- **VERIFY**: Verify driver properties compile successfully.

### Task 3: Apply Anti-Detection to Primary Scraper
- **Agent**: `backend-specialist`
- **Skill**: `clean-code`
- **Priority**: High (P0)
- **Dependencies**: Task 2
- **Description**: Update [ScrapeMatchScoresAsync](file:///Users/nnamdi/Desktop/Projects/MatchPredictor/MatchPredictor/MatchPredictor.Infrastructure/WebScraperService.cs#L94-L205) to configure options with anti-detection args. We will introduce `ConfigurePrimaryScraperBrowserOptions` or re-use `ConfigureAiScoreBrowserOptions`:
  ```csharp
  return await RunWithChromeSessionAsync(
      async driver => { ... },
      configureOptions: ConfigureAiScoreBrowserOptions,
      purpose: "score scraping");
  ```
- **INPUT**: `ScrapeMatchScoresAsync` invocation of `RunWithChromeSessionAsync`.
- **OUTPUT**: Custom configuration delegate applied to the Chrome session.
- **VERIFY**: Verify compile check.

### Task 4: Replace direct `FindElement` with WebDriverWait and Challenge Page Checks
- **Agent**: `debugger`
- **Skill**: `systematic-debugging`
- **Priority**: High (P0)
- **Dependencies**: Task 3
- **Description**: Replace the synchronous `driver.FindElement(By.Id("score-data"))` with a `WebDriverWait` that has a 15-second timeout. Before searching, verify if the page source contains a Cloudflare challenge using `LooksLikeSofaScoreChallengePage` and throw a fast-failing exception if found.
- **INPUT**: [ScrapeMatchScoresAsync](file:///Users/nnamdi/Desktop/Projects/MatchPredictor/MatchPredictor/MatchPredictor.Infrastructure/WebScraperService.cs#L94-L205) lines 105-116.
- **OUTPUT**: WebDriverWait verification and challenge check block.
- **VERIFY**: Verify compilation of `ScrapeMatchScoresAsync`.

---

## Phase X: Final Verification

- Compile check: `dotnet build`
- Integration tests: `dotnet test MatchPredictor.Tests.Integration/MatchPredictor.Tests.Integration.csproj --filter "FullyQualifiedName!~Investigate"`
- Verify that if a challenge page is encountered, it fails fast (under 30s) and registers a Cooldown/Fallback event rather than hanging for 120 seconds.
- Ensure that the Hangfire queue proceeds immediately to the next fallback service.

## ✅ PHASE X COMPLETE (Pending implementation)
- Build: ⏳ Pending
- Tests: ⏳ Pending
- Date: 2026-06-24
