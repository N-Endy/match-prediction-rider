using System;
using System.Linq;
using System.Threading.Tasks;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatchPredictor.Web.Pages.Health;

public class Health : PageModel
{
    private readonly ApplicationDbContext _dbContext;

    public Health(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IActionResult> OnGet()
    {
        var today = DateTime.UtcNow.Date;
        bool hasData = (await _dbContext.Predictions.ToListAsync())
            .Any(p => DateTime.Parse(p.Date) == today);
        return Content(hasData ? "Healthy" : "No predictions for today");
    }
}