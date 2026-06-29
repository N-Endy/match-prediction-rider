using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MatchPredictor.Domain.Models;
using MatchPredictor.Web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatchPredictor.Web.Pages;

public class ScrapeStatus : PageModel
{
    private readonly IScrapeStatusQueries _scrapeStatusQueries;
    public List<ScrapingLog> Logs { get; set; } = [];

    public ScrapeStatus(IScrapeStatusQueries scrapeStatusQueries)
    {
        _scrapeStatusQueries = scrapeStatusQueries;
    }

    public async Task OnGetAsync()
    {
        Logs = (await _scrapeStatusQueries.GetRecentLogsAsync(20)).ToList();
    }
}
