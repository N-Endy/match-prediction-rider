using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Utils;
using MatchPredictor.Web.Helpers;
using MatchPredictor.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatchPredictor.Web.Pages.Predictions;

public abstract class FilteredPredictionPageModel : PageModel
{
    protected readonly IPredictionQueries PredictionQueries;

    protected FilteredPredictionPageModel(IPredictionQueries predictionQueries)
    {
        PredictionQueries = predictionQueries;
    }

    public IReadOnlyDictionary<int, ScoreNearMissHint> ScoreNearMissHints { get; private set; } =
        new Dictionary<int, ScoreNearMissHint>();

    [BindProperty(SupportsGet = true)]
    public string Search { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string League { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string KickoffWindow { get; set; } = "all";

    [BindProperty(SupportsGet = true)]
    public string Status { get; set; } = "all";

    [BindProperty(SupportsGet = true)]
    public string ConfidenceBand { get; set; } = "all";

    public List<Prediction> Matches { get; private set; } = [];
    public List<Prediction> FilteredMatches { get; private set; } = [];
    public List<string> LeagueOptions { get; private set; } = [];
    public DateTime? LastUpdatedLocal { get; private set; }
    public string LatestRunLabel { get; private set; } = string.Empty;
    public string LatestRunReason { get; private set; } = string.Empty;

    public int TotalMatchCount => Matches.Count;
    public int VisibleMatchCount => FilteredMatches.Count;

    protected async Task<IActionResult> LoadAsync(Task<IReadOnlyList<Prediction>> resultsTask)
    {
        Matches = (await resultsTask).ToList();
        FilteredMatches = ApplyFilters(Matches).ToList();
        LeagueOptions = Matches
            .Select(match => match.League?.Trim())
            .Where(league => !string.IsNullOrWhiteSpace(league))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(league => league, StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();

        var latestPrediction = Matches
            .OrderByDescending(match => match.CreatedAt)
            .FirstOrDefault();

        if (latestPrediction is not null)
        {
            LastUpdatedLocal = DateTimeProvider.ConvertUtcToLocal(latestPrediction.CreatedAt);
            LatestRunLabel = latestPrediction.RunLabel;
            LatestRunReason = latestPrediction.RunReason;
        }

        await LoadScoreNearMissHintsAsync();
        ViewData["ScoreNearMissHints"] = ScoreNearMissHints;
        ViewData["IsAdminOperator"] = HttpContext.IsAdminOperator();

        return Page();
    }

    private async Task LoadScoreNearMissHintsAsync()
    {
        if (!HttpContext.IsAdminOperator() || FilteredMatches.Count == 0)
        {
            ScoreNearMissHints = new Dictionary<int, ScoreNearMissHint>();
            return;
        }

        var scoreLinkService = HttpContext.RequestServices.GetService<IManualScoreLinkService>();
        if (scoreLinkService is null)
        {
            ScoreNearMissHints = new Dictionary<int, ScoreNearMissHint>();
            return;
        }

        var predictionIds = FilteredMatches
            .Where(match => string.IsNullOrWhiteSpace(match.ActualScore))
            .Select(match => match.Id)
            .Distinct()
            .ToList();

        if (predictionIds.Count == 0)
        {
            ScoreNearMissHints = new Dictionary<int, ScoreNearMissHint>();
            return;
        }

        ScoreNearMissHints = await scoreLinkService.GetHintsAsync(predictionIds);
    }

    private IEnumerable<Prediction> ApplyFilters(IEnumerable<Prediction> predictions)
    {
        var nowUtc = DateTime.UtcNow;
        var normalizedSearch = (Search ?? string.Empty).Trim();
        var normalizedLeague = (League ?? string.Empty).Trim();
        var normalizedKickoff = string.IsNullOrWhiteSpace(KickoffWindow)
            ? "all"
            : KickoffWindow.Trim().ToLowerInvariant();
        var normalizedStatus = string.IsNullOrWhiteSpace(Status)
            ? "all"
            : Status.Trim().ToLowerInvariant();
        var normalizedConfidence = string.IsNullOrWhiteSpace(ConfidenceBand)
            ? "all"
            : ConfidenceBand.Trim().ToLowerInvariant();

        foreach (var prediction in predictions)
        {
            if (!string.IsNullOrWhiteSpace(normalizedSearch))
            {
                var haystack = $"{prediction.League} {prediction.HomeTeam} {prediction.AwayTeam} {prediction.PredictedOutcome}";
                if (!haystack.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            if (!string.IsNullOrWhiteSpace(normalizedLeague) &&
                !string.Equals(prediction.League, normalizedLeague, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!MatchesKickoffWindow(prediction, normalizedKickoff))
            {
                continue;
            }

            if (!MatchesStatus(prediction, normalizedStatus, nowUtc))
            {
                continue;
            }

            if (!MatchesConfidence(prediction, normalizedConfidence))
            {
                continue;
            }

            yield return prediction;
        }
    }

    private static bool MatchesKickoffWindow(Prediction prediction, string kickoffWindow)
    {
        if (string.IsNullOrWhiteSpace(kickoffWindow) || kickoffWindow == "all")
        {
            return true;
        }

        var localTime = prediction.MatchLocalTime ?? DateTimeProvider.ParseLocalTimeOrNull(prediction.Time);
        if (!localTime.HasValue)
        {
            return true;
        }

        var hour = localTime.Value.Hour;
        return kickoffWindow switch
        {
            "morning" => hour is >= 0 and < 12,
            "afternoon" => hour is >= 12 and < 17,
            "evening" => hour is >= 17 and < 21,
            "late" => hour is >= 21,
            _ => true
        };
    }

    private static bool MatchesStatus(Prediction prediction, string status, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(status) || status == "all")
        {
            return true;
        }

        var isLive = PredictionDisplayHelper.IsActuallyLive(prediction, nowUtc);
        var isFinished = !isLive && !string.IsNullOrWhiteSpace(prediction.ActualScore);
        var isUpcoming = !isLive && !isFinished;

        return status switch
        {
            "upcoming" => isUpcoming,
            "live" => isLive,
            "finished" => isFinished,
            _ => true
        };
    }

    private static bool MatchesConfidence(Prediction prediction, string confidenceBand)
    {
        if (string.IsNullOrWhiteSpace(confidenceBand) || confidenceBand == "all")
        {
            return true;
        }

        var confidence = PredictionDisplayHelper.GetConfidenceValue(prediction);
        return confidenceBand switch
        {
            "elite" => confidence >= 0.80m,
            "strong" => confidence >= 0.70m,
            "watch" => confidence >= 0.60m,
            _ => true
        };
    }
}
