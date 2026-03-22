using MatchPredictor.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatchPredictor.Web.Pages;

public class AiChatModel : PageModel
{
    private readonly IConfiguration _configuration;
    private readonly IAiChatAuthTicketService _authTicketService;

    public AiChatModel(
        IConfiguration configuration,
        IAiChatAuthTicketService authTicketService)
    {
        _configuration = configuration;
        _authTicketService = authTicketService;
    }

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    public bool RequiresPassword { get; private set; }

    public bool IsAuthenticated { get; private set; }

    public string? ErrorMessage { get; private set; }

    public string AssistantName => "Nelson";

    public void OnGet()
    {
        RequiresPassword = IsAuthenticationRequired();

        if (!RequiresPassword)
        {
            _authTicketService.SignIn(HttpContext);
            IsAuthenticated = true;
            return;
        }

        IsAuthenticated = _authTicketService.IsAuthenticated(HttpContext);
    }

    public IActionResult OnPost()
    {
        RequiresPassword = IsAuthenticationRequired();

        if (!RequiresPassword)
        {
            _authTicketService.SignIn(HttpContext);
            return RedirectToPage();
        }

        var configuredPassword = _configuration["AiChatPassword"]?.Trim();
        if (string.IsNullOrWhiteSpace(configuredPassword))
        {
            _authTicketService.SignIn(HttpContext);
            return RedirectToPage();
        }

        if (!string.Equals(Password?.Trim(), configuredPassword, StringComparison.Ordinal))
        {
            ErrorMessage = "That password didn't match the configured AI chat password.";
            IsAuthenticated = false;
            return Page();
        }

        _authTicketService.SignIn(HttpContext);
        return RedirectToPage();
    }

    public IActionResult OnPostLogout()
    {
        _authTicketService.SignOut(HttpContext);
        return RedirectToPage();
    }

    private bool IsAuthenticationRequired()
    {
        return !string.IsNullOrWhiteSpace(_configuration["AiChatPassword"]);
    }
}
