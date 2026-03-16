using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.AspNetCore.Mvc;

namespace MatchPredictor.Web.Pages.Predictions;

public class BTTS : FilteredPredictionPageModel
{
    public BTTS(IPredictionQueries predictionQueries)
        : base(predictionQueries)
    {
    }
    
    public async Task<IActionResult> OnGet()
    {
        return await LoadAsync(PredictionQueries.GetBTTSAsync(DateTimeProvider.GetLocalTime()));
    }
}
