using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using Xunit;
using Xunit.Abstractions;

namespace MatchPredictor.Tests.Integration
{
    public class AiScoreInvestigateTests
    {
        private readonly ITestOutputHelper _output;

        public AiScoreInvestigateTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task InvestigateAiScore()
        {
            var chromeOptions = new ChromeOptions();
            chromeOptions.AddArgument("--headless=new");
            chromeOptions.AddArgument("--window-size=1440,900");
            chromeOptions.AddArgument("--no-sandbox");
            chromeOptions.AddArgument("--disable-dev-shm-usage");
            chromeOptions.AddArgument("--disable-blink-features=AutomationControlled");
            chromeOptions.AddExcludedArgument("enable-automation");
            chromeOptions.AddAdditionalOption("useAutomationExtension", false);
            chromeOptions.AddArgument("--user-agent=Mozilla/5.0 (Linux; Android 13; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36");
            chromeOptions.AddArgument("--disable-gpu");

            using var driver = new ChromeDriver(chromeOptions);
            var js = (IJavaScriptExecutor)driver;
            js.ExecuteScript("Object.defineProperty(navigator, 'webdriver', {get: () => undefined})");

            _output.WriteLine("Navigating to AiScore...");
            await driver.Navigate().GoToUrlAsync("https://m.aiscore.com");

            var maxWait = 25;
            var elapsed = 0;
            while (elapsed < maxWait)
            {
                await Task.Delay(2000);
                elapsed += 2;
                var src = driver.PageSource;
                if (!src.Contains("security verification", StringComparison.OrdinalIgnoreCase) &&
                    !src.Contains("cf-turnstile", StringComparison.OrdinalIgnoreCase) &&
                    !src.Contains("Just a moment", StringComparison.OrdinalIgnoreCase))
                {
                    _output.WriteLine($"Cloudflare passed after {elapsed}s.");
                    break;
                }
            }

            await Task.Delay(5000);
            
            var pageSource = driver.PageSource;
            File.WriteAllText("/tmp/aiscore_source_csharp.html", pageSource);
            _output.WriteLine("Saved page source to /tmp/aiscore_source_csharp.html");

            // Look for __NUXT__ or NEXT_DATA__
            var isNuxt = (bool)js.ExecuteScript("return !!window.__NUXT__;");
            var isNext = (bool)js.ExecuteScript("return !!window.__NEXT_DATA__;");
            
            _output.WriteLine($"Is Nuxt: {isNuxt}");
            _output.WriteLine($"Is Next: {isNext}");
            
            // Get all window keys that look like initialState or data
            var keys = (string)js.ExecuteScript("return Object.keys(window).filter(k => k.startsWith('__')).join(', ');");
            _output.WriteLine($"Window '__' keys: {keys}");
            
            var nextData = (string)js.ExecuteScript("return window.__NEXT_DATA__ ? JSON.stringify(window.__NEXT_DATA__).substring(0, 500) : 'none';");
            _output.WriteLine($"Next Data preview: {nextData}");

            Assert.True(true);
        }
    }
}
