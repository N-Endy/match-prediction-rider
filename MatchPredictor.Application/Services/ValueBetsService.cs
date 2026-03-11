using System.Text.Json;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatchPredictor.Application.Services;

public class ValueBetsService : IValueBetsService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IProbabilityCalculator _probabilityCalculator;
    private readonly IAiAdvisorService _aiAdvisorService;
    private readonly ILogger<ValueBetsService> _logger;

    public ValueBetsService(
        ApplicationDbContext dbContext,
        IProbabilityCalculator probabilityCalculator,
        IAiAdvisorService aiAdvisorService,
        ILogger<ValueBetsService> logger)
    {
        _dbContext = dbContext;
        _probabilityCalculator = probabilityCalculator;
        _aiAdvisorService = aiAdvisorService;
        _logger = logger;
    }

    public async Task<IEnumerable<ValueBetDto>> GetTopValueBetsAsync(int limit = 60, CancellationToken ct = default)
    {
        // 1. Fetch upcoming matches
        var now = DateTimeProvider.GetLocalTime();
        var todayStr = now.ToString("dd-MM-yyyy");
        var currentTime = now.ToString("HH:mm");

        // Filter to today's matches that haven't kicked off yet (or tomorrow's if we want broader scope)
        var upcomingMatches = await _dbContext.MatchDatas
            .Where(m => m.Date == todayStr && string.Compare(m.Time, currentTime) >= 0)
            .ToListAsync(ct);

        if (!upcomingMatches.Any())
        {
            _logger.LogInformation("No upcoming matches available for Value Bets today.");
            return Enumerable.Empty<ValueBetDto>();
        }

        var accuracies = await _dbContext.ModelAccuracies.ToListAsync(ct);

        var candidateBets = new List<ValueBetDto>();

        // 2. Compute probabilities
        foreach (var match in upcomingMatches)
        {
            var probs = new Dictionary<string, (string outcome, double prob)>
            {
                { "BothTeamsScore", ("BTTS", _probabilityCalculator.CalculateBttsProbability(match, accuracies)) },
                { "Over2.5Goals", ("Over 2.5", _probabilityCalculator.CalculateOverTwoGoalsProbability(match, accuracies)) },
                { "StraightWin", ("Home Win", _probabilityCalculator.CalculateHomeWinProbability(match, accuracies)) },
                { "StraightWinAway", ("Away Win", _probabilityCalculator.CalculateAwayWinProbability(match, accuracies)) },
                { "Draw", ("Draw", _probabilityCalculator.CalculateDrawProbability(match, accuracies)) }
            };

            // Add ALL markets above threshold per match (Fix #12)
            foreach (var market in probs.Where(p => p.Value.prob > 0.75))
            {
                var category = market.Key == "StraightWinAway" ? "StraightWin" : market.Key;
                
                candidateBets.Add(new ValueBetDto
                {
                    League = match.League,
                    HomeTeam = match.HomeTeam,
                    AwayTeam = match.AwayTeam,
                    KickoffTime = match.Time,
                    PredictionCategory = category,
                    PredictedOutcome = market.Value.outcome,
                    MathematicalProbability = market.Value.prob,
                    AiJustification = "" // To be filled by AI
                });
            }
        }

        // 3. Take Top N
        var topCandidates = candidateBets
            .OrderByDescending(c => c.MathematicalProbability)
            .Take(limit)
            .ToList();

        if (!topCandidates.Any())
        {
            return Enumerable.Empty<ValueBetDto>();
        }

        // 4. Serialize to prompt
        var payloadToAnalyze = JsonSerializer.Serialize(topCandidates.Select(c => new {
            c.League,
            c.HomeTeam,
            c.AwayTeam,
            c.KickoffTime,
            c.PredictionCategory,
            c.PredictedOutcome,
            MathProb = Math.Round(c.MathematicalProbability * 100, 1) + "%"
        }));

        // 5. Ask Groq to Analyze
        var aiResponseJson = await _aiAdvisorService.AnalyzeValueBetsAsync(payloadToAnalyze, ct);

        if (aiResponseJson.StartsWith("❌") || aiResponseJson.StartsWith("⏳") || aiResponseJson.StartsWith("⚠️"))
        {
            _logger.LogWarning("AI Advisor returned an error/warning: {Message}", aiResponseJson);
            throw new InvalidOperationException(aiResponseJson);
        }

        // 6. Parse and Merge
        try
        {
            var aiPicks = JsonSerializer.Deserialize<List<ValueBetDto>>(aiResponseJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            
            if (aiPicks != null)
            {
                foreach (var pick in aiPicks)
                {
                    var original = topCandidates.FirstOrDefault(c => 
                        c.HomeTeam == pick.HomeTeam && c.AwayTeam == pick.AwayTeam);
                    
                    if (original != null)
                    {
                        original.AiJustification = pick.AiJustification;
                    }
                }
            }

            // Return only the ones Groq decided to keep and justify
            return topCandidates.Where(c => !string.IsNullOrEmpty(c.AiJustification)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse JSON response from Groq for Value Bets. Raw Response: {Raw}", aiResponseJson);
            throw; // Re-throw to ensure the Controller catches it and returns a 500 to trigger the UI Error State
        }
    }
}
