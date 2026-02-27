using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;

namespace MatchPredictor.Web.Pages;

public class AiChatModel : PageModel
{
    private readonly IConfiguration _config;
    public bool IsAuthenticated { get; set; }
    [BindProperty] public string? Password { get; set; }
    public string? ErrorMessage { get; set; }

    public AiChatModel(IConfiguration config)
    {
        _config = config;
    }

    public void OnGet()
    {
        IsAuthenticated = Request.Cookies.ContainsKey("MP_AI_AUTH");
    }

    public IActionResult OnPost()
    {
        var validPassword = _config["AiChatPassword"] ?? "Match2026!";
        
        if (Password == validPassword)
        {
            Response.Cookies.Append("MP_AI_AUTH", "true", new CookieOptions
            {
                Expires = DateTime.UtcNow.AddDays(30),
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict
            });
            return RedirectToPage();
        }

        ErrorMessage = "Incorrect password.";
        IsAuthenticated = false;
        return Page();
    }
}
