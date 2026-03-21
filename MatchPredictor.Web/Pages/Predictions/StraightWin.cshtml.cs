using Microsoft.AspNetCore.Mvc;

namespace MatchPredictor.Web.Pages.Predictions;

public class StraightWin : Microsoft.AspNetCore.Mvc.RazorPages.PageModel
{
    public IActionResult OnGet()
    {
        return RedirectToPage("/Predictions/MatchWinner");
    }
}
