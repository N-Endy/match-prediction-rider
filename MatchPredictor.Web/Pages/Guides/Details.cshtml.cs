using System.Text.Json;
using MatchPredictor.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatchPredictor.Web.Pages.Guides;

public class DetailsModel : PageModel
{
    private readonly IGuideContentService _guideService;

    public DetailsModel(IGuideContentService guideService)
    {
        _guideService = guideService;
    }

    public GuideArticle Article { get; set; } = null!;
    public string CanonicalUrl { get; set; } = string.Empty;
    public string JsonLd { get; set; } = string.Empty;

    public IActionResult OnGet(string slug)
    {
        var article = _guideService.GetGuideBySlug(slug);
        if (article is null)
        {
            return NotFound();
        }

        Article = article;
        var origin = $"{Request.Scheme}://{Request.Host.Value}";
        CanonicalUrl = $"{origin}/guides/{slug}";

        ViewData["Title"] = $"{article.Title} – MatchPredictor";
        ViewData["Description"] = article.Summary;

        var schemaData = new Dictionary<string, object?>
        {
            ["@context"] = "https://schema.org",
            ["@type"] = "Article",
            ["headline"] = article.Title,
            ["description"] = article.Summary,
            ["author"] = new Dictionary<string, string>
            {
                ["@type"] = "Organization",
                ["name"] = article.AuthorName,
                ["url"] = origin
            },
            ["publisher"] = new Dictionary<string, string>
            {
                ["@type"] = "Organization",
                ["name"] = "MatchPredictor",
                ["url"] = origin
            },
            ["mainEntityOfPage"] = CanonicalUrl,
            ["datePublished"] = "2026-09-15T00:00:00Z"
        };

        JsonLd = JsonSerializer.Serialize(schemaData, new JsonSerializerOptions { WriteIndented = false });

        return Page();
    }
}
