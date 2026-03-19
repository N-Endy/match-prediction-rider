using MatchPredictor.Domain.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace MatchPredictor.Web.Pages.Predictions;

public class Draw : FilteredPredictionPageModel
{
    public Draw(IPredictionQueries predictionQueries)
        : base(predictionQueries)
    {
    }

    public IActionResult OnGet()
    {
        return RedirectToPage("/Predictions/Under2");
    }
}
