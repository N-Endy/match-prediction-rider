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
    public bool ShowLogout { get; set; }
    [BindProperty] public string? Password { get; set; }
    public string? ErrorMessage { get; set; }

    public AiChatModel(IConfiguration config, IAiChatAuthTicketService authTicketService)
    {
        _config = config;
        _authTicketService = authTicketService;
    }

    public void OnGet()
    {
        if (!_authTicketService.IsLoginRequired)
        {
            _authTicketService.EnsureSession(HttpContext);
            IsAuthenticated = true;
            ShowLogout = false;
            return;
        }

        IsAuthenticated = _authTicketService.IsAuthenticated(HttpContext);
        ShowLogout = IsAuthenticated;
    }

    public IActionResult OnPost()
    {
        if (!_authTicketService.IsLoginRequired)
        {
            _authTicketService.EnsureSession(HttpContext);
            return RedirectToPage();
        }

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
        ShowLogout = false;
        return Page();
    }

    public IActionResult OnPostLogout()
    {
        _authTicketService.SignOut(HttpContext);
        return RedirectToPage();
    }
}
