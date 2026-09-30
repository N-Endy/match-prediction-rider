using MatchPredictor.Web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatchPredictor.Web.Pages.Guides;

public class IndexModel : PageModel
{
    private readonly IGuideContentService _guideService;

    public IndexModel(IGuideContentService guideService)
    {
        _guideService = guideService;
    }

    public IReadOnlyList<GuideArticle> Guides { get; set; } = [];

    public void OnGet()
    {
        Guides = _guideService.GetAllGuides();
        ViewData["Title"] = "Football Predictive Modeling Guides & Sports Analytics Knowledge Base";
        ViewData["Description"] = "In-depth mathematical analysis, Dixon-Coles Poisson modeling, probability calibration, and sports analytics research papers by MatchPredictor.";
    }
}
