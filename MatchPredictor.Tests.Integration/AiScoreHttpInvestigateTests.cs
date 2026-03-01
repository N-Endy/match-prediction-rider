using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace MatchPredictor.Tests.Integration
{
    public class AiScoreHttpInvestigateTests
    {
        private readonly ITestOutputHelper _output;

        public AiScoreHttpInvestigateTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task InvestigateAiScoreHttp()
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            
            var response = await client.GetAsync("https://m.aiscore.com");
            _output.WriteLine($"Status: {response.StatusCode}");
            
            var html = await response.Content.ReadAsStringAsync();
            
            _output.WriteLine($"HTML length: {html.Length}");
            
            var match = Regex.Match(html, @"window\.__NUXT__=(.*?);</script>");
            if (match.Success)
            {
                _output.WriteLine("Found __NUXT__ object!");
                var nuxtStr = match.Groups[1].Value;
                _output.WriteLine($"Length of JSON string: {nuxtStr.Length}");
            }
            else
            {
                _output.WriteLine("Did NOT find __NUXT__.");
                if (html.Contains("cloudflare") || html.Contains("Just a moment"))
                {
                    _output.WriteLine("Blocked by Cloudflare.");
                }
            }
        }
    }
}
