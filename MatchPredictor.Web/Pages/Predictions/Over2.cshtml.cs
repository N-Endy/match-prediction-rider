using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatchPredictor.Web.Pages.Predictions;

public class Over2 : PageModel
{
    private readonly IPredictionQueries _predictionQueries;
    public List<Prediction>? Matches { get; set; } = [];
    
    public Over2(IPredictionQueries predictionQueries)
    {
        _predictionQueries = predictionQueries;
    }
    
    public async Task<IActionResult> OnGet()
    {
        var today = DateTimeProvider.GetLocalTime();
        var results = await _predictionQueries.GetOver25Async(today);
        Matches = results.ToList();

        return Page();
    }
}