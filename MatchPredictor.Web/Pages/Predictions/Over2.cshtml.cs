using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.AspNetCore.Mvc;

namespace MatchPredictor.Web.Pages.Predictions;

public class Over2 : FilteredPredictionPageModel
{
    public Over2(IPredictionQueries predictionQueries)
        : base(predictionQueries)
    {
    }
    
    public async Task<IActionResult> OnGet()
    {
        return await LoadAsync(PredictionQueries.GetOver25Async(DateTimeProvider.GetLocalTime()));
    }
}
