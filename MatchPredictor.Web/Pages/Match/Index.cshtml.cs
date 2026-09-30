using System.Text.Json;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Web.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatchPredictor.Web.Pages.Match;

public class IndexModel : PageModel
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IAiChatFootballInsightService _insightService;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        ApplicationDbContext dbContext,
        IAiChatFootballInsightService insightService,
        ILogger<IndexModel> logger)
    {
        _dbContext = dbContext;
        _insightService = insightService;
        _logger = logger;
    }

    public Prediction CurrentPrediction { get; set; } = null!;
    public List<Prediction> RelatedPredictions { get; set; } = [];
    public FootballMatchInsightSnapshot? FootballInsight { get; set; }
    public string CanonicalSlug { get; set; } = string.Empty;
    public string CanonicalUrl { get; set; } = string.Empty;
    public string JsonLd { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(int id, string? slug, CancellationToken ct)
    {
        var prediction = await _dbContext.Predictions
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (prediction is null || !prediction.WasPublished)
        {
            return NotFound();
        }

        CurrentPrediction = prediction;
        CanonicalSlug = PredictionDisplayHelper.GenerateMatchSlug(prediction.HomeTeam, prediction.AwayTeam);

        if (!string.IsNullOrEmpty(slug) && !string.Equals(slug, CanonicalSlug, StringComparison.OrdinalIgnoreCase))
        {
            return RedirectToPagePermanent("/Match/Index", new { id, slug = CanonicalSlug });
        }

        var origin = $"{Request.Scheme}://{Request.Host.Value}";
        CanonicalUrl = $"{origin}/match/{id}/{CanonicalSlug}";

        ViewData["Title"] = $"{prediction.HomeTeam} vs {prediction.AwayTeam} Prediction & Statistical Analysis";
        ViewData["Description"] = $"Calibrated match analysis, Poisson scoreline model probabilities, recent form, and head-to-head metrics for {prediction.HomeTeam} vs {prediction.AwayTeam} ({prediction.League}).";

        // Query all related published market predictions for this fixture
        var fixtureKey = prediction.FixtureKey;
        var relatedQuery = _dbContext.Predictions
            .AsNoTracking()
            .Where(p => p.WasPublished);

        if (!string.IsNullOrEmpty(fixtureKey))
        {
            relatedQuery = relatedQuery.Where(p => p.FixtureKey == fixtureKey);
        }
        else
        {
            relatedQuery = relatedQuery.Where(p =>
                p.HomeTeam == prediction.HomeTeam &&
                p.AwayTeam == prediction.AwayTeam &&
                p.MatchLocalDate == prediction.MatchLocalDate);
        }

        RelatedPredictions = await relatedQuery
            .OrderBy(p => p.PredictionCategory)
            .ToListAsync(ct);

        // Fetch deep match insights (form, H2H, goals per match)
        try
        {
            var insightRequest = new AiChatFootballInsightRequest
            {
                ActionKey = $"match-{prediction.Id}",
                HomeTeam = prediction.HomeTeam,
                AwayTeam = prediction.AwayTeam,
                League = prediction.League,
                PredictionCategory = prediction.PredictionCategory,
                PredictedOutcome = prediction.PredictedOutcome,
                MatchDateTimeUtc = prediction.MatchDateTime,
                MatchLocalDate = prediction.MatchLocalDate,
                KickoffTime = prediction.Time ?? string.Empty
            };

            var insights = await _insightService.GetInsightsAsync([insightRequest], ct);
            if (insights.TryGetValue(insightRequest.ActionKey, out var insight))
            {
                FootballInsight = insight;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load football insight snapshot for match ID {Id}", id);
        }

        // Generate SportsEvent Schema.org JSON-LD
        var schemaData = new Dictionary<string, object?>
        {
            ["@context"] = "https://schema.org",
            ["@type"] = "SportsEvent",
            ["name"] = $"{prediction.HomeTeam} vs {prediction.AwayTeam}",
            ["sport"] = "Soccer",
            ["startDate"] = prediction.MatchDateTime?.ToString("O"),
            ["competitor"] = new object[]
            {
                new Dictionary<string, string> { ["@type"] = "SportsTeam", ["name"] = prediction.HomeTeam },
                new Dictionary<string, string> { ["@type"] = "SportsTeam", ["name"] = prediction.AwayTeam }
            },
            ["location"] = new Dictionary<string, string>
            {
                ["@type"] = "Place",
                ["name"] = $"{prediction.HomeTeam} Stadium"
            },
            ["description"] = ViewData["Description"]
        };

        JsonLd = JsonSerializer.Serialize(schemaData, new JsonSerializerOptions { WriteIndented = false });

        return Page();
    }
}
