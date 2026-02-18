using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MatchPredictor.Web.Pages.Predictions;

public class Combined : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly IMemoryCache _cache;
    public List<Prediction>? Matches { get; set; } = [];
    
    public Combined(ApplicationDbContext context, IMemoryCache cache)
    {
        _cache = cache;
        _context = context;
    }
    
    public async Task<IActionResult> OnGet()
    {
        var dateString = DateTime.UtcNow.Date.ToString("dd-MM-yyyy");
        var today = DateTime.UtcNow.Date;
        var random = new Random();
        Matches = await _cache.GetOrCreateAsync($"combined_{today}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(12);
            return await _context.Predictions
                .Where(p => p.Date == dateString)
                .ToListAsync();
        });
            
        Matches = Matches?
            .OrderBy(_ => random.Next())
            .Take(30)
            .DistinctBy(p => new { p.League, p.HomeTeam, p.AwayTeam, p.Date, p.Time })
            .OrderBy(p => p.Time)
            .ThenBy(p => p.League)
            .ThenBy(p => p.HomeTeam)
            .ToList();
        
        return Page();
    }
}