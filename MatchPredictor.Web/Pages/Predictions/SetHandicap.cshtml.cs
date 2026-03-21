using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.AspNetCore.Mvc;

namespace MatchPredictor.Web.Pages.Predictions;

public class SetHandicap : FilteredPredictionPageModel
{
    public SetHandicap(IPredictionQueries predictionQueries)
        : base(predictionQueries)
    {
    }

    public async Task<IActionResult> OnGet()
    {
        return await LoadAsync(PredictionQueries.GetSetHandicapAsync(DateTimeProvider.GetLocalTime()));
    }
}
