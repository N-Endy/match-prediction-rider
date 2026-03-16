using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.AspNetCore.Mvc;

namespace MatchPredictor.Web.Pages.Predictions;

public class Combined : FilteredPredictionPageModel
{
    public Combined(IPredictionQueries predictionQueries)
        : base(predictionQueries)
    {
    }
    
    public async Task<IActionResult> OnGet()
    {
        return await LoadAsync(PredictionQueries.GetCombinedSampleAsync(DateTimeProvider.GetLocalTime(), 60));
    }
}
