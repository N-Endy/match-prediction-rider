using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using MatchPredictor.Web.Middleware;
using MatchPredictor.Web.Services;

namespace MatchPredictor.Web.Pages;

[ValidateAntiForgeryToken]
public class AiChatModel : PageModel
{
    private readonly IConfiguration _config;
    private readonly IAiChatAuthTicketService _authTicketService;
    public bool IsAuthenticated { get; set; }
    [BindProperty] public string? Password { get; set; }
    public string? ErrorMessage { get; set; }

    public AiChatModel(IConfiguration config, IAiChatAuthTicketService authTicketService)
    {
        _config = config;
        _authTicketService = authTicketService;
    }

    public void OnGet()
    {
        IsAuthenticated = _authTicketService.IsAuthenticated(HttpContext);
    }

    public IActionResult OnPost()
    {
        var validPassword = _config["AiChatPassword"];
        
        if (!string.IsNullOrEmpty(validPassword) &&
            !string.IsNullOrEmpty(Password) &&
            AdminUsageBasicAuthMiddleware.FixedTimeEquals(Password, validPassword))
        {
            _authTicketService.SignIn(HttpContext);
            return RedirectToPage();
        }

        ErrorMessage = "Incorrect password.";
        IsAuthenticated = false;
        return Page();
    }

    public IActionResult OnPostLogout()
    {
        _authTicketService.SignOut(HttpContext);
        return RedirectToPage();
    }
}
