using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatchPredictor.Web.Pages.Predictions;

public class Combined : PageModel
{
    private readonly IPredictionQueries _predictionQueries;
    public List<Prediction>? Matches { get; set; } = [];
    
    public Combined(IPredictionQueries predictionQueries)
    {
        _predictionQueries = predictionQueries;
    }
    
    public async Task<IActionResult> OnGet()
    {
        var today = DateTimeProvider.GetLocalTime();
        var results = await _predictionQueries.GetCombinedSampleAsync(today, 30);
        Matches = results.ToList();

        return Page();
    }
}