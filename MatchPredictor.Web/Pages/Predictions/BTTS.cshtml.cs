using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MatchPredictor.Application.Helpers;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MatchPredictor.Web.Pages.Predictions;

public class BTTS : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly IMemoryCache _cache;
    public List<Prediction>? Matches { get; set; } = [];
    
    public BTTS(ApplicationDbContext context, IMemoryCache cache)
    {
        _cache = cache;
        _context = context;
    }
    
    public async Task<IActionResult> OnGet()
    {
        var dateString = DateTimeProvider.GetLocalTimeString();
        var today = DateTimeProvider.GetLocalTime();
        Matches = await _cache.GetOrCreateAsync($"btts_{today}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);
            return await _context.Predictions
                .Where(p => p.Date == dateString &&
                            p.PredictionCategory == "BothTeamsScore")
                .OrderBy(p => p.Time)
                .ThenBy(p => p.League)
                .ThenBy(p => p.HomeTeam)
                .ToListAsync();
        });
            
        Matches = Matches?
            .DistinctBy(p => new { p.League, p.HomeTeam, p.AwayTeam, p.Date, p.Time })
            .ToList();

        // Fallback: patch any predictions missing scores by matching against MatchScores
        if (Matches?.Any(m => string.IsNullOrEmpty(m.ActualScore)) == true)
        {
            var todayScores = await _context.MatchScores
                .Where(s => s.MatchTime.Date == today.Date)
                .ToListAsync();

            ScoreMatchingHelper.PatchMissingScores(Matches, todayScores);
        }
        
        return Page();
    }
}