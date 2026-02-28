using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Utils;

namespace MatchPredictor.Web.Pages;

public class AnalyticsModel : PageModel
{
    private readonly ApplicationDbContext _db;
    private readonly IConfiguration _config;

    public bool IsAuthenticated { get; set; }
    [BindProperty] public string? Password { get; set; }
    public string? ErrorMessage { get; set; }

    // Analytics Data Models
    public AnalyticsStats TodayStats { get; set; } = new();
    public AnalyticsStats YesterdayStats { get; set; } = new();
    public AnalyticsStats Last3DaysStats { get; set; } = new();
    public AnalyticsStats Last7DaysStats { get; set; } = new();

    public AnalyticsModel(ApplicationDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    public async Task OnGetAsync()
    {
        IsAuthenticated = Request.Cookies.ContainsKey("MP_AI_AUTH");

        if (IsAuthenticated)
        {
            await LoadAnalyticsDataAsync();
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var validPassword = _config["AiChatPassword"];
        
        if (!string.IsNullOrEmpty(validPassword) && Password == validPassword)
        {
            Response.Cookies.Append("MP_AI_AUTH", "true", new CookieOptions
            {
                Expires = DateTime.UtcNow.AddDays(30),
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict
            });
            IsAuthenticated = true;
            await LoadAnalyticsDataAsync();
            return RedirectToPage();
        }

        ErrorMessage = "Incorrect password.";
        IsAuthenticated = false;
        return Page();
    }

    private async Task LoadAnalyticsDataAsync()
    {
        var today = DateTimeProvider.GetLocalTime().Date;
        
        var dateStringsToday = new[] { today.ToString("dd-MM-yyyy") };
        var dateStringsYesterday = new[] { today.AddDays(-1).ToString("dd-MM-yyyy") };
        var dateStringsLast3 = Enumerable.Range(0, 3).Select(i => today.AddDays(-i).ToString("dd-MM-yyyy")).ToArray();
        var dateStringsLast7 = Enumerable.Range(0, 7).Select(i => today.AddDays(-i).ToString("dd-MM-yyyy")).ToArray();

        // 1. Fetch Predictions
        var last7Predictions = await _db.Predictions
            .Where(p => dateStringsLast7.Contains(p.Date))
            .ToListAsync();

        TodayStats = CalculateStats(last7Predictions.Where(p => dateStringsToday.Contains(p.Date)));
        YesterdayStats = CalculateStats(last7Predictions.Where(p => dateStringsYesterday.Contains(p.Date)));
        Last3DaysStats = CalculateStats(last7Predictions.Where(p => dateStringsLast3.Contains(p.Date)));
        Last7DaysStats = CalculateStats(last7Predictions);
    }

    private AnalyticsStats CalculateStats(IEnumerable<Prediction> predictions)
    {
        var list = predictions.ToList();
        var stats = new AnalyticsStats
        {
            TotalPredictions = list.Count,
            CompletedPredictions = list.Count(p => !string.IsNullOrEmpty(p.ActualOutcome) && !p.IsLive)
        };

        if (stats.CompletedPredictions > 0)
        {
            var completed = list.Where(p => !string.IsNullOrEmpty(p.ActualOutcome) && !p.IsLive).ToList();
            stats.CorrectPredictions = completed.Count(p => p.PredictedOutcome == p.ActualOutcome);
            stats.OverallAccuracy = (double)stats.CorrectPredictions / stats.CompletedPredictions;

            // Group by category
            var grouped = completed.GroupBy(p => p.PredictionCategory);
            foreach (var group in grouped)
            {
                var total = group.Count();
                var correct = group.Count(p => p.PredictedOutcome == p.ActualOutcome);
                stats.CategoryStats[group.Key] = new CategoryStat
                {
                    Category = group.Key,
                    Total = total,
                    Correct = correct,
                    Accuracy = (double)correct / total
                };
            }
        }

        return stats;
    }
}

public class AnalyticsStats
{
    public int TotalPredictions { get; set; }
    public int CompletedPredictions { get; set; }
    public int CorrectPredictions { get; set; }
    public double OverallAccuracy { get; set; }
    public Dictionary<string, CategoryStat> CategoryStats { get; set; } = new();
}

public class CategoryStat
{
    public string Category { get; set; } = string.Empty;
    public int Total { get; set; }
    public int Correct { get; set; }
    public double Accuracy { get; set; }
}
