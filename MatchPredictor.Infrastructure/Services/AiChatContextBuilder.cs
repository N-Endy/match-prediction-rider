using System.Globalization;
using System.Text.RegularExpressions;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Utils;

namespace MatchPredictor.Infrastructure.Services;

public static partial class AiChatContextBuilder
{
    private static readonly HashSet<string> GenericPromptTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "about", "acca", "accumulator", "add", "all", "analysis", "analyse", "analyze", "any", "another",
        "away", "banker", "bankers", "best", "bet", "bets", "book", "booking", "both", "btts", "can", "chat",
        "combo", "draw", "for", "game", "games", "give", "goals", "good", "help", "home", "i", "in", "into", "is",
        "it", "leg", "legs", "list", "match", "matches", "me", "need", "of", "on", "open", "over", "pick", "picks",
        "prediction", "predictions", "safe", "safer", "score", "show", "slip", "some", "straight", "strong",
        "stronger", "teams", "the", "them", "these", "this", "those", "ticket", "to", "today", "top", "value",
        "want", "what", "which", "win", "wins", "with", "you"
    };

    public static AiChatContextSelection BuildSelection(
        IEnumerable<Prediction> predictions,
        string userPrompt,
        DateTime nowUtc,
        int limit = 40)
    {
        var nowLocal = DateTimeProvider.ConvertUtcToLocal(nowUtc);
        var todayStr = nowLocal.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
        var candidates = predictions
            .Where(prediction => prediction.Date == todayStr && prediction.WasPublished)
            .Where(prediction => prediction.MatchDateTime is null || prediction.MatchDateTime >= nowUtc)
            .Select(CreateCandidate)
            .ToList();

        if (candidates.Count == 0)
        {
            return new AiChatContextSelection
            {
                Candidates = [],
                TotalAvailableCount = 0
            };
        }

        var promptTokens = Tokenize(userPrompt);
        var marketFilters = DetectMarketFilters(promptTokens);
        var specificTokens = promptTokens
            .Where(token => !GenericPromptTokens.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ranked = candidates
            .Select(candidate => CreateRankedCandidate(candidate, promptTokens, marketFilters))
            .ToList();

        if (specificTokens.Count > 0)
        {
            var entityMatched = ranked
                .Where(item => item.EntityMatchCount > 0)
                .ToList();

            if (entityMatched.Count == 0)
            {
                return new AiChatContextSelection
                {
                    Candidates = [],
                    TotalAvailableCount = candidates.Count,
                    NoRelevantMatchesFound = true
                };
            }

            var maxEntityMatchCount = entityMatched.Max(item => item.EntityMatchCount);
            ranked = entityMatched
                .Where(item => item.EntityMatchCount == maxEntityMatchCount)
                .ToList();
        }
        else if (marketFilters.Count > 0)
        {
            ranked = ranked
                .Where(item => marketFilters.Contains(item.Candidate.PredictionCategory))
                .ToList();
        }

        var selected = ranked
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Candidate.ConfidenceScore ?? decimal.Zero)
            .Take(limit)
            .Select(item => item.Candidate)
            .ToList();

        return new AiChatContextSelection
        {
            Candidates = selected,
            TotalAvailableCount = candidates.Count,
            NoRelevantMatchesFound = selected.Count == 0 && specificTokens.Count > 0
        };
    }

    public static string CreateActionKey(Prediction prediction) => $"P{prediction.Id}";

    public static string ToCartMarket(Prediction prediction)
    {
        return prediction.PredictionCategory switch
        {
            "BothTeamsScore" => "BTTS",
            "Over2.5Goals" => "Over2.5",
            _ => "1X2"
        };
    }

    public static string BuildNoRelevantMatchesMessage(string userPrompt)
    {
        var cleanedPrompt = userPrompt.Trim();
        if (string.IsNullOrWhiteSpace(cleanedPrompt))
        {
            return "I couldn't find that in today's published predictions. Ask about a team, league, or market that appears on today's card.";
        }

        return $"I couldn't find a matching team, league, or fixture for \"{cleanedPrompt}\" in today's published predictions.";
    }

    private static AiChatContextCandidate CreateCandidate(Prediction prediction)
    {
        var confidence = prediction.ConfidenceScore ?? prediction.RawConfidenceScore ?? 0m;
        var rawConfidence = prediction.RawConfidenceScore ?? prediction.ConfidenceScore ?? 0m;
        var searchableText = $"{prediction.HomeTeam} {prediction.AwayTeam} {prediction.League}";

        return new AiChatContextCandidate
        {
            ActionKey = CreateActionKey(prediction),
            PredictionId = prediction.Id,
            League = prediction.League,
            KickoffTime = prediction.Time,
            HomeTeam = prediction.HomeTeam,
            AwayTeam = prediction.AwayTeam,
            PredictionCategory = prediction.PredictionCategory,
            PredictedOutcome = prediction.PredictedOutcome,
            ConfidenceScore = confidence,
            RawConfidenceScore = rawConfidence,
            ThresholdUsed = prediction.ThresholdUsed,
            ThresholdSource = prediction.ThresholdSource,
            CalibratorUsed = prediction.CalibratorUsed,
            WasPublished = prediction.WasPublished,
            MarginAboveThreshold = Math.Round((double)confidence - prediction.ThresholdUsed, 4),
            SearchTokens = Tokenize(searchableText)
        };
    }

    private static RankedCandidate CreateRankedCandidate(
        AiChatContextCandidate candidate,
        HashSet<string> promptTokens,
        HashSet<string> marketFilters)
    {
        var entityMatches = candidate.SearchTokens.Intersect(promptTokens, StringComparer.OrdinalIgnoreCase).Count();
        var score = (double)(candidate.ConfidenceScore ?? decimal.Zero) * 100d;
        score += candidate.MarginAboveThreshold * 150d;

        if (marketFilters.Count > 0)
        {
            score += marketFilters.Contains(candidate.PredictionCategory) ? 50d : -200d;
        }

        if (promptTokens.Contains("safe") || promptTokens.Contains("banker") || promptTokens.Contains("bankers"))
        {
            score += candidate.PredictionCategory == "StraightWin" ? 20d : 0d;
            score -= candidate.PredictionCategory == "Draw" ? 10d : 0d;
        }

        if (promptTokens.Contains("value"))
        {
            score += candidate.MarginAboveThreshold * 120d;
        }

        score += entityMatches * 40d;

        return new RankedCandidate(candidate, score, entityMatches);
    }

    private static HashSet<string> DetectMarketFilters(HashSet<string> promptTokens)
    {
        var filters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (promptTokens.Contains("btts") || (promptTokens.Contains("both") && promptTokens.Contains("score")))
        {
            filters.Add("BothTeamsScore");
        }

        if (promptTokens.Contains("over") || promptTokens.Contains("goals"))
        {
            filters.Add("Over2.5Goals");
        }

        if (promptTokens.Contains("draw"))
        {
            filters.Add("Draw");
        }

        if (promptTokens.Contains("straight") ||
            promptTokens.Contains("win") ||
            promptTokens.Contains("home") ||
            promptTokens.Contains("away"))
        {
            filters.Add("StraightWin");
        }

        return filters;
    }

    private static HashSet<string> Tokenize(string value)
    {
        return TokenRegex()
            .Matches(value.ToLowerInvariant())
            .Select(match => match.Value)
            .Where(token => token.Length >= 3)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    [GeneratedRegex("[a-z0-9]+", RegexOptions.Compiled)]
    private static partial Regex TokenRegex();

    public sealed class AiChatContextSelection
    {
        public IReadOnlyList<AiChatContextCandidate> Candidates { get; init; } = [];
        public int TotalAvailableCount { get; init; }
        public bool NoRelevantMatchesFound { get; init; }
    }

    public sealed class AiChatContextCandidate
    {
        public string ActionKey { get; init; } = string.Empty;
        public int PredictionId { get; init; }
        public string League { get; init; } = string.Empty;
        public string KickoffTime { get; init; } = string.Empty;
        public string HomeTeam { get; init; } = string.Empty;
        public string AwayTeam { get; init; } = string.Empty;
        public string PredictionCategory { get; init; } = string.Empty;
        public string PredictedOutcome { get; init; } = string.Empty;
        public decimal? ConfidenceScore { get; init; }
        public decimal? RawConfidenceScore { get; init; }
        public double ThresholdUsed { get; init; }
        public string ThresholdSource { get; init; } = string.Empty;
        public string CalibratorUsed { get; init; } = string.Empty;
        public bool WasPublished { get; init; }
        public double MarginAboveThreshold { get; init; }
        internal HashSet<string> SearchTokens { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record RankedCandidate(AiChatContextCandidate Candidate, double Score, int EntityMatchCount)
    {
    }
}
