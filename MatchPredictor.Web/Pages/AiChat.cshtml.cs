using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;

namespace MatchPredictor.Web.Pages;

public class AiChatModel : PageModel
{
    private const string AuthCookieName = "MP_AI_AUTH";
    private const string SessionCookieName = "MP_AI_CHAT_SESSION";
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
        IsAuthenticated = Request.Cookies.ContainsKey(AuthCookieName);
    }

    public IActionResult OnPost()
    {
        var validPassword = _config["AiChatPassword"];
        
        if (!string.IsNullOrEmpty(validPassword) && Password == validPassword)
        {
            Response.Cookies.Append(AuthCookieName, "true", new CookieOptions
            {
                Expires = DateTime.UtcNow.AddDays(30),
                HttpOnly = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Strict
            });

            if (!Request.Cookies.ContainsKey(SessionCookieName))
            {
                Response.Cookies.Append(SessionCookieName, Guid.NewGuid().ToString("N"), new CookieOptions
                {
                    HttpOnly = true,
                    Secure = Request.IsHttps,
                    SameSite = SameSiteMode.Strict
                });
            }

            return RedirectToPage();
        }

        ErrorMessage = "Incorrect password.";
        IsAuthenticated = false;
        return Page();
    }
}
