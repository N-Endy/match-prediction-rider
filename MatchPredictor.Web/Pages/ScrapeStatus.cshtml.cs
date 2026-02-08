using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatchPredictor.Web.Pages;

public class ScrapeStatus : PageModel
{
    private readonly ApplicationDbContext _dbContext;
    public List<ScrapingLog> Logs { get; set; }

    public ScrapeStatus(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task OnGetAsync()
    {
        Logs = await _dbContext.ScrapingLogs
            .OrderByDescending(x => x.Timestamp)
            .Take(20)
            .ToListAsync();
    }
}