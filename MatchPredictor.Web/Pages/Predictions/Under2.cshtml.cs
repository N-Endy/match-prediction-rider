using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.AspNetCore.Mvc;

namespace MatchPredictor.Web.Pages.Predictions;

public class Under2 : FilteredPredictionPageModel
{
    public Under2(IPredictionQueries predictionQueries)
        : base(predictionQueries)
    {
    }

    public async Task<IActionResult> OnGet()
    {
        return await LoadAsync(PredictionQueries.GetUnder25Async(DateTimeProvider.GetLocalTime()));
    }
}
