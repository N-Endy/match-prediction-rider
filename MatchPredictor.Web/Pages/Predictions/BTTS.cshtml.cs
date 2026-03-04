using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatchPredictor.Web.Pages.Predictions;

public class BTTS : PageModel
{
    private readonly IPredictionQueries _predictionQueries;
    public List<Prediction>? Matches { get; set; } = [];
    
    public BTTS(IPredictionQueries predictionQueries)
    {
        _predictionQueries = predictionQueries;
    }
    
    public async Task<IActionResult> OnGet()
    {
        var today = DateTimeProvider.GetLocalTime();
        var results = await _predictionQueries.GetBTTSAsync(today);
        Matches = results.ToList();
        
        return Page();
    }
}