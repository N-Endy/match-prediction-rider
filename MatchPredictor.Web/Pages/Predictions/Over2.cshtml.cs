using Microsoft.AspNetCore.Mvc;

namespace MatchPredictor.Web.Pages.Predictions;

public class Over2 : Microsoft.AspNetCore.Mvc.RazorPages.PageModel
{
    public IActionResult OnGet()
    {
        return RedirectToPage("/Predictions/OverUnderSets");
    }
}
