using System.Diagnostics;
using System.Text.Json;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace MatchPredictor.Infrastructure.Services;

public class DeepMatchResearchService : IDeepMatchResearchService
{
    private static readonly TimeSpan DefaultResearchTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ApplicationDbContext _dbContext;
    private readonly IAiChatFootballInsightService _footballInsightService;
    private readonly IDistributedCache _cache;
    private readonly ILogger<DeepMatchResearchService> _logger;

    public DeepMatchResearchService(
        ApplicationDbContext dbContext,
        IAiChatFootballInsightService footballInsightService,
        IDistributedCache cache,
        ILogger<DeepMatchResearchService> logger)
    {
        _dbContext = dbContext;
        _footballInsightService = footballInsightService;
        _cache = cache;
        _logger = logger;
    }

    public async Task<DeepMatchResearchDossier> ResearchFixtureAsync(
        string homeTeam,
        string awayTeam,
        DateTime? fixtureDateUtc = null,
        string? league = null,
        CancellationToken ct = default)
    {
        var targetDate = fixtureDateUtc ?? DateTime.UtcNow;
        var cleanHome = (homeTeam ?? string.Empty).Trim();
        var cleanAway = (awayTeam ?? string.Empty).Trim();
        var cacheKey = $"deep_research:{cleanHome.ToLowerInvariant()}:{cleanAway.ToLowerInvariant()}:{targetDate:yyyyMMdd}";

        // Check cache first
        try
        {
            var cachedJson = await _cache.GetStringAsync(cacheKey, ct);
            if (!string.IsNullOrWhiteSpace(cachedJson))
            {
                var cachedDossier = JsonSerializer.Deserialize<DeepMatchResearchDossier>(cachedJson, JsonOptions);
                if (cachedDossier != null)
                {
                    _logger.LogDebug("Deep match research cache hit for {Home} vs {Away}", cleanHome, cleanAway);
                    return cachedDossier;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read deep research cache for {Home} vs {Away}", cleanHome, cleanAway);
        }

        var sw = Stopwatch.StartNew();

        // Enforce 6s timeout budget for external and intensive lookups
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(DefaultResearchTimeout);

        var dossier = new DeepMatchResearchDossier
        {
            FixtureKey = $"{cleanHome}_{cleanAway}_{targetDate:yyyyMMdd}",
            HomeTeam = cleanHome,
            AwayTeam = cleanAway,
            League = league ?? "General",
            MatchDateUtc = targetDate
        };

        try
        {
            // 1. Fetch internal published predictions and pricing for this matchup
            await PopulateInternalModelSignalsAsync(dossier, cleanHome, cleanAway, targetDate, cts.Token);

            // 2. Fetch Form, Venue Splits, and Head-to-Head insights
            await PopulateFootballInsightsAsync(dossier, cleanHome, cleanAway, targetDate, cts.Token);

            // 3. Synthesize Dixon-Coles goal expectancy and Elo ratings
            SynthesizeStatisticalModels(dossier);

            // 4. Synthesize tactical narrative & conviction
            SynthesizeTacticalVerdict(dossier);

            dossier.ResearchLatency = sw.Elapsed;
            dossier.DataQuality = dossier.FormAndH2H?.DataQuality ?? "Standard";

            // Cache the result
            try
            {
                var serialized = JsonSerializer.Serialize(dossier, JsonOptions);
                await _cache.SetStringAsync(cacheKey, serialized, new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = CacheTtl
                }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to write deep research cache for {Home} vs {Away}", cleanHome, cleanAway);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Deep match research exceeded 6s timeout for {Home} vs {Away}. Delivering synthesized internal baseline.", cleanHome, cleanAway);
            SynthesizeStatisticalModels(dossier);
            SynthesizeTacticalVerdict(dossier);
            dossier.ResearchLatency = sw.Elapsed;
            dossier.TacticalFactors.Add("Live external updates timed out after 6 seconds; evaluation based on internal historical models.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error conducting deep match research for {Home} vs {Away}", cleanHome, cleanAway);
            dossier.ResearchLatency = sw.Elapsed;
            dossier.ResearchSummary = $"Research completed with available internal data for {cleanHome} vs {cleanAway}.";
        }

        return dossier;
    }

    private async Task PopulateInternalModelSignalsAsync(
        DeepMatchResearchDossier dossier,
        string homeTeam,
        string awayTeam,
        DateTime targetDate,
        CancellationToken ct)
    {
        var windowStart = DateOnly.FromDateTime(targetDate.AddDays(-2));
        var windowEnd = DateOnly.FromDateTime(targetDate.AddDays(3));

        var predictions = await _dbContext.Predictions
            .AsNoTracking()
            .Where(p => p.MatchLocalDate >= windowStart && p.MatchLocalDate <= windowEnd)
            .Where(p => (p.HomeTeam.ToLower() == homeTeam.ToLower() && p.AwayTeam.ToLower() == awayTeam.ToLower()) ||
                        p.HomeTeam.ToLower().Contains(homeTeam.ToLower()) ||
                        p.AwayTeam.ToLower().Contains(awayTeam.ToLower()))
            .ToListAsync(ct);

        if (predictions.Count == 0)
        {
            return;
        }

        foreach (var pred in predictions)
        {
            var conf = (double)(pred.ConfidenceScore ?? decimal.Zero);
            var category = (pred.PredictionCategory ?? string.Empty).ToLowerInvariant();

            if (category.Contains("straight") || category.Contains("1x2") || category.Contains("home"))
            {
                if (pred.PredictedOutcome.Equals("Home", StringComparison.OrdinalIgnoreCase) ||
                    pred.PredictedOutcome.Equals(homeTeam, StringComparison.OrdinalIgnoreCase))
                {
                    dossier.CalibratedHomeWinProb = Math.Max(dossier.CalibratedHomeWinProb, conf);
                    if (string.IsNullOrWhiteSpace(dossier.RecommendedPick) && conf >= 0.55)
                    {
                        dossier.RecommendedPick = $"Home Win ({homeTeam})";
                    }
                }
                else if (pred.PredictedOutcome.Equals("Away", StringComparison.OrdinalIgnoreCase) ||
                         pred.PredictedOutcome.Equals(awayTeam, StringComparison.OrdinalIgnoreCase))
                {
                    dossier.CalibratedAwayWinProb = Math.Max(dossier.CalibratedAwayWinProb, conf);
                    if (string.IsNullOrWhiteSpace(dossier.RecommendedPick) && conf >= 0.55)
                    {
                        dossier.RecommendedPick = $"Away Win ({awayTeam})";
                    }
                }
            }
            else if (category.Contains("draw"))
            {
                dossier.CalibratedDrawProb = Math.Max(dossier.CalibratedDrawProb, conf);
            }
            else if (category.Contains("over") || category.Contains("2.5"))
            {
                dossier.CalibratedOver25Prob = Math.Max(dossier.CalibratedOver25Prob, conf);
                if (conf >= 0.65)
                {
                    dossier.RecommendedPick = "Over 2.5 Goals";
                }
            }
            else if (category.Contains("btts"))
            {
                dossier.CalibratedBttsProb = Math.Max(dossier.CalibratedBttsProb, conf);
            }

            if (!string.IsNullOrWhiteSpace(pred.League) && dossier.League == "General")
            {
                dossier.League = pred.League;
            }
        }
    }

    private async Task PopulateFootballInsightsAsync(
        DeepMatchResearchDossier dossier,
        string homeTeam,
        string awayTeam,
        DateTime targetDate,
        CancellationToken ct)
    {
        var request = new AiChatFootballInsightRequest
        {
            ActionKey = dossier.FixtureKey,
            HomeTeam = homeTeam,
            AwayTeam = awayTeam,
            MatchLocalDate = DateOnly.FromDateTime(targetDate),
            MatchDateTimeUtc = targetDate,
            League = dossier.League
        };

        var insights = await _footballInsightService.GetInsightsAsync([request], ct);
        if (insights.TryGetValue(dossier.FixtureKey, out var snapshot))
        {
            dossier.FormAndH2H = snapshot;
            dossier.ExternalDataIncluded = snapshot.InsightSource.Contains("External", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void SynthesizeStatisticalModels(DeepMatchResearchDossier dossier)
    {
        var homeForm = dossier.FormAndH2H?.HomeForm;
        var awayForm = dossier.FormAndH2H?.AwayForm;

        // Dixon-Coles expected goals proxy (Home Attack vs Away Defense / Away Attack vs Home Defense)
        double homeAttack = homeForm?.VenueGoalsForPerMatch > 0 ? homeForm.VenueGoalsForPerMatch : (homeForm?.GoalsForPerMatch ?? 1.4);
        double awayDefense = awayForm?.VenueGoalsAgainstPerMatch > 0 ? awayForm.VenueGoalsAgainstPerMatch : (awayForm?.GoalsAgainstPerMatch ?? 1.2);
        dossier.HomeExpectedGoals = Math.Round((homeAttack + awayDefense) / 2.0, 2);

        double awayAttack = awayForm?.VenueGoalsForPerMatch > 0 ? awayForm.VenueGoalsForPerMatch : (awayForm?.GoalsForPerMatch ?? 1.1);
        double homeDefense = homeForm?.VenueGoalsAgainstPerMatch > 0 ? homeForm.VenueGoalsAgainstPerMatch : (homeForm?.GoalsAgainstPerMatch ?? 1.1);
        dossier.AwayExpectedGoals = Math.Round((awayAttack + homeDefense) / 2.0, 2);

        // Elo ratings baseline & adjustment
        double baseElo = 1500;
        double homePpm = homeForm?.PointsPerMatch ?? 1.4;
        double awayPpm = awayForm?.PointsPerMatch ?? 1.2;
        dossier.HomeEloRating = Math.Round(baseElo + ((homePpm - 1.3) * 150), 0);
        dossier.AwayEloRating = Math.Round(baseElo + ((awayPpm - 1.3) * 150), 0);
    }

    private static void SynthesizeTacticalVerdict(DeepMatchResearchDossier dossier)
    {
        var home = dossier.HomeTeam;
        var away = dossier.AwayTeam;
        var hForm = dossier.FormAndH2H?.HomeForm;
        var aForm = dossier.FormAndH2H?.AwayForm;
        var h2h = dossier.FormAndH2H?.HeadToHead;

        var tacticalNotes = new List<string>();

        if (hForm != null && hForm.SampleSize > 0)
        {
            tacticalNotes.Add($"{home} has averaged {hForm.PointsPerMatch:F1} points/game with {hForm.GoalsForPerMatch:F1} goals scored across their last {hForm.SampleSize} fixtures.");
            if (hForm.CleanSheetRate >= 0.40)
            {
                tacticalNotes.Add($"{home} has kept clean sheets in {(hForm.CleanSheetRate * 100):F0}% of recent matches.");
            }
        }

        if (aForm != null && aForm.SampleSize > 0)
        {
            tacticalNotes.Add($"{away} has conceded {aForm.GoalsAgainstPerMatch:F1} goals/game in recent away/overall outings.");
        }

        if (h2h != null && h2h.SampleSize > 0)
        {
            tacticalNotes.Add($"Head-to-head record across {h2h.SampleSize} encounters: {home} {h2h.HomeTeamWins}W - {h2h.Draws}D - {away} {h2h.AwayTeamWins}W.");
        }

        if (dossier.HomeExpectedGoals > 0 && dossier.AwayExpectedGoals > 0)
        {
            tacticalNotes.Add($"Expected Goals (xG) Projection: {home} {dossier.HomeExpectedGoals:F2} - {dossier.AwayExpectedGoals:F2} {away}.");
        }

        dossier.TacticalFactors = tacticalNotes;

        // Conviction Level
        if (dossier.CalibratedHomeWinProb >= 0.70 || dossier.CalibratedAwayWinProb >= 0.70 || dossier.CalibratedOver25Prob >= 0.75)
        {
            dossier.ConvictionLevel = "High";
        }
        else if (dossier.CalibratedHomeWinProb >= 0.55 || dossier.CalibratedAwayWinProb >= 0.55 || dossier.CalibratedOver25Prob >= 0.60)
        {
            dossier.ConvictionLevel = "Moderate";
        }
        else
        {
            dossier.ConvictionLevel = "Cautious";
        }

        var pick = !string.IsNullOrWhiteSpace(dossier.RecommendedPick) ? dossier.RecommendedPick : (dossier.HomeExpectedGoals > dossier.AwayExpectedGoals ? $"{home} to avoid defeat" : "Competitive clash");
        dossier.ResearchSummary = $"Match Analysis ({dossier.ConvictionLevel} Conviction): {home} vs {away}. Primary pick indicator: {pick}. xG model projects {dossier.HomeExpectedGoals:F2} vs {dossier.AwayExpectedGoals:F2}.";
    }
}
