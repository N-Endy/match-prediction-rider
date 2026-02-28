using System;
using System.Net.Http;
using System.Threading.Tasks;

class Program
{
    static async Task Main()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("clientid", "web");
        client.DefaultRequestHeaders.TryAddWithoutValidation("platform", "web");

        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // market 1 (1X2), 18 (Over/Under), 29 (GG/NG)
        var url = $"https://www.sportybet.com/api/ng/factsCenter/pcUpcomingEvents?sportId=sr%3Asport%3A1&marketId=1,18,29&pageSize=10&pageNum=1&todayGames=true&timeline=2.9&_t={ts}";
        
        var resp = await client.GetAsync(url);
        var json = await resp.Content.ReadAsStringAsync();
        Console.WriteLine(json);
    }
}
