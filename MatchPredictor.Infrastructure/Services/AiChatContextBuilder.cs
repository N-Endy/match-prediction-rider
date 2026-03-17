using System.Globalization;
using System.Text.RegularExpressions;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Utils;

namespace MatchPredictor.Infrastructure.Services;

public static partial class AiChatContextBuilder
{
    private const int MaxRequestedCandidates = 60;

    private static readonly HashSet<string> GenericPromptTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "about", "acca", "accumulator", "add", "all", "analysis", "analyse", "analyze", "any", "another", "are",
        "and", "away", "banker", "bankers", "best", "bet", "bets", "book", "booking", "both", "btts", "can", "chat",
        "combo", "combination", "day", "days", "doing", "draw", "for", "game", "games", "give", "goals", "good", "help", "home", "i", "in", "into", "is",
        "it", "leg", "legs", "list", "match", "matches", "me", "need", "odd", "odds", "of", "on", "open", "over", "pick", "picks",
        "prediction", "predictions", "recent", "recommend", "recommended", "recommending", "recommendation", "recommendations", "result", "results", "safe", "safer", "score", "settle", "settled", "show", "slip", "some", "straight", "strong",
        "straightwin", "straightwins", "stronger", "rollover", "teams", "the", "them", "these", "this", "those", "ticket", "to",
        "today", "top", "value", "why", "won", "yesterday",
        "want", "what", "which", "win", "wins", "with", "would", "you", "your", "red", "green", "finished", "lost", "landed", "did", "mix", "mixture", "suggest", "suggested",
        "explain", "explained", "discuss", "discussion", "talk", "riskiest", "weakest", "remove", "swap", "replace", "fits"
    };

    private static readonly HashSet<string> RecommendationTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "best", "safe", "safer", "strong", "stronger", "top", "pick", "picks", "list", "show", "give", "recommend", "recommended", "recommendation", "recommendations", "suggest", "suggested", "mix", "mixture", "combo", "combination"
    };

    private static readonly HashSet<string> SettlementTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "finished", "settle", "settled", "result", "results", "red", "green", "won", "lost", "landed"
    };

    private static readonly HashSet<string> AppHelpTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "analytics", "analytic", "brier", "reliability", "resolution", "uncertainty", "threshold", "thresholds",
        "calibrator", "calibration", "value", "pricing", "freshness", "edge", "exclusion", "excluded", "source",
        "chip", "chips", "live", "upcoming", "finished", "selected", "selection", "meaning", "mean"
    };

    public static AiChatContextSelection BuildSelection(
        IEnumerable<Prediction> predictions,
        string userPrompt,
        DateTime nowUtc,
        IReadOnlyDictionary<int, AiChatCandidatePricing>? pricingByPredictionId = null,
        int limit = 40)
    {
        var nowLocal = DateTimeProvider.ConvertUtcToLocal(nowUtc);
        var todayLocalDate = DateOnly.FromDateTime(nowLocal);
        var isRolloverRequest = MentionsRolloverIntent(userPrompt);
        var hasTargetCombinedOdds = TryExtractRolloverTargetOdds(userPrompt, out var requestedCombinedOdds);
        var candidates = BuildCandidateCatalog(predictions, nowUtc, pricingByPredictionId);

        if (candidates.Count == 0)
        {
            return new AiChatContextSelection
            {
                Candidates = [],
                TotalAvailableCount = 0
            };
        }

        var promptTokens = Tokenize(userPrompt);
        var requestedMarketSlices = ExtractRequestedMarketSlices(userPrompt);
        var marketFilters = DetectMarketFilters(promptTokens);
        if (requestedMarketSlices.Count == 0 && marketFilters.Count > 1)
        {
            requestedMarketSlices = BuildImplicitMarketSlices(userPrompt, marketFilters);
        }

        var genericRequestedCount = ExtractGenericRequestedCount(userPrompt, requestedMarketSlices, marketFilters, promptTokens);
        var specificTokens = promptTokens
            .Where(token => !GenericPromptTokens.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectionIntent = DetectSelectionIntent(
            promptTokens,
            specificTokens.Count > 0,
            requestedMarketSlices.Count > 0,
            genericRequestedCount,
            isRolloverRequest);
        var requestedCandidateCount = requestedMarketSlices.Sum(slice => slice.Count);
        if (requestedCandidateCount == 0 && genericRequestedCount > 0)
        {
            requestedCandidateCount = genericRequestedCount;
        }
        var shouldTreatPromptAsGenericMarketRequest = ShouldTreatPromptAsGenericMarketRequest(
            specificTokens,
            promptTokens,
            marketFilters,
            requestedMarketSlices,
            genericRequestedCount,
            isRolloverRequest);

        var ranked = candidates
            .Where(candidate => MatchesSelectionIntent(candidate, selectionIntent, todayLocalDate))
            .Select(candidate => CreateRankedCandidate(candidate, promptTokens, marketFilters, isRolloverRequest, selectionIntent, todayLocalDate))
            .ToList();

        if (specificTokens.Count > 0 && !shouldTreatPromptAsGenericMarketRequest)
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
                    NoRelevantMatchesFound = true,
                    RequestedMarketSlices = requestedMarketSlices,
                    RequestedCandidateCount = requestedCandidateCount,
                    IsRolloverRequest = isRolloverRequest,
                    RequestedCombinedOdds = hasTargetCombinedOdds ? requestedCombinedOdds : null,
                    NeedsRolloverTargetOdds = isRolloverRequest && !hasTargetCombinedOdds,
                    DateScopeLabel = selectionIntent.DisplayLabel
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

        var orderedRanked = ranked
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Candidate.ConfidenceScore ?? decimal.Zero)
            .ThenByDescending(item => item.Candidate.EdgePoints ?? double.MinValue)
            .ToList();

        var selected = requestedMarketSlices.Count > 0
            ? SelectRequestedMarketSlices(orderedRanked, requestedMarketSlices, limit)
            : orderedRanked
                .Take(isRolloverRequest ? limit : ResolveSelectionLimit(limit, requestedCandidateCount))
                .Select(item => item.Candidate)
                .ToList();

        return new AiChatContextSelection
        {
            Candidates = selected,
            TotalAvailableCount = candidates.Count,
            NoRelevantMatchesFound = selected.Count == 0 && specificTokens.Count > 0,
            RequestedMarketSlices = requestedMarketSlices,
            RequestedCandidateCount = requestedCandidateCount,
            IsRolloverRequest = isRolloverRequest,
            RequestedCombinedOdds = hasTargetCombinedOdds ? requestedCombinedOdds : null,
            NeedsRolloverTargetOdds = isRolloverRequest && !hasTargetCombinedOdds,
            DateScopeLabel = selectionIntent.DisplayLabel
        };
    }

    public static string CreateActionKey(Prediction prediction) => $"P{prediction.Id}";

    public static IReadOnlyList<AiChatContextCandidate> BuildCandidateCatalog(
        IEnumerable<Prediction> predictions,
        DateTime nowUtc,
        IReadOnlyDictionary<int, AiChatCandidatePricing>? pricingByPredictionId = null)
    {
        var todayLocalDate = DateOnly.FromDateTime(DateTimeProvider.ConvertUtcToLocal(nowUtc));
        var recentStartDate = todayLocalDate.AddDays(-7);

        return predictions
            .Where(prediction => prediction.IsCurrentRevision && prediction.WasPublished)
            .Where(prediction => prediction.MatchLocalDate >= recentStartDate && prediction.MatchLocalDate <= todayLocalDate)
            .Select(prediction => CreateCandidate(prediction, pricingByPredictionId?.GetValueOrDefault(prediction.Id), nowUtc, todayLocalDate))
            .ToList();
    }

    public static AiChatIntent DetectIntent(
        string userPrompt,
        bool hasWorkingSlip,
        bool hasContextCandidates)
    {
        var prompt = userPrompt.ToLowerInvariant();
        var tokens = Tokenize(userPrompt);
        var marketFilters = DetectMarketFilters(tokens);
        var requestedSlices = ExtractRequestedMarketSlices(userPrompt);

        if (ContainsSecuritySensitiveTopic(prompt))
        {
            return AiChatIntent.SecurityRefusal;
        }

        if (IsWorkingSlipRefinementPrompt(prompt, hasWorkingSlip))
        {
            return AiChatIntent.WorkingSlipRefinement;
        }

        if (IsSettlementPrompt(prompt, hasContextCandidates))
        {
            return AiChatIntent.SettlementExplanation;
        }

        if (IsAppHelpPrompt(prompt, tokens))
        {
            return AiChatIntent.AppHelp;
        }

        if (IsMatchDiscussionPrompt(prompt, hasWorkingSlip, hasContextCandidates))
        {
            return AiChatIntent.MatchDiscussion;
        }

        if (requestedSlices.Count > 1 || marketFilters.Count > 1 || prompt.Contains("mixture", StringComparison.Ordinal) || prompt.Contains("mix", StringComparison.Ordinal))
        {
            return AiChatIntent.MixedMarketRecommendation;
        }

        return AiChatIntent.RecommendPicks;
    }

    public static bool MentionsRolloverIntent(string userPrompt)
    {
        if (string.IsNullOrWhiteSpace(userPrompt))
        {
            return false;
        }

        var prompt = userPrompt.ToLowerInvariant();
        return prompt.Contains("rollover", StringComparison.Ordinal) ||
               prompt.Contains("roll over", StringComparison.Ordinal);
    }

    public static bool TryExtractRolloverTargetOdds(string userPrompt, out double targetOdds)
    {
        targetOdds = 0;

        if (string.IsNullOrWhiteSpace(userPrompt))
        {
            return false;
        }

        var oddsText = RolloverTargetOddsRegex().Match(userPrompt).Groups["odds"].Value;
        if (string.IsNullOrWhiteSpace(oddsText))
        {
            oddsText = GenericOddsRegex().Match(userPrompt).Groups["odds"].Value;
        }

        if (string.IsNullOrWhiteSpace(oddsText))
        {
            oddsText = BareOddsRegex().Match(userPrompt).Groups["odds"].Value;
        }

        if (string.IsNullOrWhiteSpace(oddsText))
        {
            return false;
        }

        return double.TryParse(
                   oddsText.Replace(',', '.'),
                   NumberStyles.AllowDecimalPoint,
                   CultureInfo.InvariantCulture,
                   out targetOdds) &&
               targetOdds >= 1.05 &&
               targetOdds <= 100.0;
    }

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
            return "I couldn't find that in the current published prediction window. Ask about a team, league, market, or recent fixture that appears on the card.";
        }

        return $"I couldn't find a matching team, league, or fixture for \"{cleanedPrompt}\" in the current published prediction window.";
    }

    public static bool IsContextFollowUpPrompt(string userPrompt)
    {
        if (string.IsNullOrWhiteSpace(userPrompt))
        {
            return false;
        }

        var prompt = userPrompt.ToLowerInvariant();
        return (prompt.Contains("this", StringComparison.Ordinal) ||
                prompt.Contains("that", StringComparison.Ordinal) ||
                prompt.Contains("it", StringComparison.Ordinal) ||
                prompt.Contains("them", StringComparison.Ordinal)) &&
               (prompt.Contains("settle", StringComparison.Ordinal) ||
                prompt.Contains("result", StringComparison.Ordinal) ||
                prompt.Contains("red", StringComparison.Ordinal) ||
                prompt.Contains("green", StringComparison.Ordinal) ||
                prompt.Contains("why", StringComparison.Ordinal) ||
                prompt.Contains("explain", StringComparison.Ordinal) ||
                prompt.Contains("talk", StringComparison.Ordinal));
    }

    private static AiChatContextCandidate CreateCandidate(
        Prediction prediction,
        AiChatCandidatePricing? pricing,
        DateTime nowUtc,
        DateOnly todayLocalDate)
    {
        var confidence = prediction.ConfidenceScore ?? prediction.RawConfidenceScore ?? 0m;
        var rawConfidence = prediction.RawConfidenceScore ?? prediction.ConfidenceScore ?? 0m;
        var searchableText = $"{prediction.HomeTeam} {prediction.AwayTeam} {prediction.League}";
        var marketProbability = pricing?.MarketProbability;
        var modelProbability = (double)confidence;
        var edgePoints = marketProbability is > 0
            ? Math.Round((modelProbability - marketProbability.Value) * 100d, 2)
            : (double?)null;
        var isLive = prediction.IsLive &&
                     (!prediction.MatchDateTime.HasValue || prediction.MatchDateTime.Value.AddMinutes(200) >= nowUtc);
        var isFinished = !isLive && !string.IsNullOrWhiteSpace(prediction.ActualScore);
        var isUpcoming = !isLive && !isFinished;
        var hasNotStarted = !prediction.MatchDateTime.HasValue || prediction.MatchDateTime.Value >= nowUtc;
        var canBook = prediction.MatchLocalDate == todayLocalDate && isUpcoming && hasNotStarted;
        var matchState = isLive ? "Live" : isFinished ? "Finished" : "Upcoming";

        return new AiChatContextCandidate
        {
            ActionKey = CreateActionKey(prediction),
            PredictionId = prediction.Id,
            MatchLocalDate = prediction.MatchLocalDate,
            League = prediction.League,
            KickoffTime = prediction.MatchLocalTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? prediction.Time,
            HomeTeam = prediction.HomeTeam,
            AwayTeam = prediction.AwayTeam,
            PredictionCategory = prediction.PredictionCategory,
            PredictedOutcome = prediction.PredictedOutcome,
            ActualScore = prediction.ActualScore,
            ActualOutcome = prediction.ActualOutcome,
            ConfidenceScore = confidence,
            RawConfidenceScore = rawConfidence,
            ThresholdUsed = prediction.ThresholdUsed,
            ThresholdSource = prediction.ThresholdSource,
            CalibratorUsed = prediction.CalibratorUsed,
            WasPublished = prediction.WasPublished,
            MatchState = matchState,
            CanBook = canBook,
            MarginAboveThreshold = Math.Round((double)confidence - prediction.ThresholdUsed, 4),
            MarketProbability = marketProbability,
            EstimatedOdds = pricing?.EstimatedDecimalOdds,
            EdgePoints = edgePoints,
            FixtureKey = $"{prediction.League}|{prediction.HomeTeam}|{prediction.AwayTeam}|{prediction.Time}",
            SearchTokens = Tokenize(searchableText)
        };
    }

    private static RankedCandidate CreateRankedCandidate(
        AiChatContextCandidate candidate,
        HashSet<string> promptTokens,
        HashSet<string> marketFilters,
        bool isRolloverRequest,
        SelectionIntent selectionIntent,
        DateOnly todayLocalDate)
    {
        var entityMatches = candidate.SearchTokens.Intersect(promptTokens, StringComparer.OrdinalIgnoreCase).Count();
        var score = (double)(candidate.ConfidenceScore ?? decimal.Zero) * 100d;
        score += candidate.MarginAboveThreshold * 150d;
        score += (candidate.EdgePoints ?? 0d) * 3d;
        score += GetDateRecencyBoost(candidate.MatchLocalDate, todayLocalDate);
        score += candidate.CanBook ? 8d : 0d;

        if (marketFilters.Count > 0)
        {
            score += marketFilters.Contains(candidate.PredictionCategory) ? 50d : -200d;
        }

        if (selectionIntent.Scope == DateScope.RecentFinished && candidate.MatchState == "Finished")
        {
            score += 16d;
        }

        if (selectionIntent.Scope == DateScope.Yesterday && candidate.MatchLocalDate == todayLocalDate.AddDays(-1))
        {
            score += 22d;
        }

        if (promptTokens.Contains("safe") || promptTokens.Contains("banker") || promptTokens.Contains("bankers"))
        {
            score += candidate.PredictionCategory == "StraightWin" ? 20d : 0d;
            score -= candidate.PredictionCategory == "Draw" ? 10d : 0d;

            if (candidate.EstimatedOdds is > 0)
            {
                score += candidate.EstimatedOdds <= 1.65 ? 12d : Math.Max(-18d, 12d - ((candidate.EstimatedOdds.Value - 1.65d) * 20d));
            }
        }

        if (promptTokens.Contains("value"))
        {
            score += candidate.MarginAboveThreshold * 120d;
        }

        if (isRolloverRequest && candidate.EstimatedOdds is > 0)
        {
            score += candidate.EstimatedOdds <= 1.85 ? 8d : 4d;
        }

        score += entityMatches * 40d;

        return new RankedCandidate(candidate, score, entityMatches);
    }

    private static double GetDateRecencyBoost(DateOnly matchDate, DateOnly todayLocalDate)
    {
        var daysBack = todayLocalDate.DayNumber - matchDate.DayNumber;
        return daysBack switch
        {
            <= 0 => 10d,
            1 => 7d,
            2 => 4d,
            3 => 2d,
            _ => Math.Max(0d, 1d - ((daysBack - 3) * 0.25d))
        };
    }

    private static SelectionIntent DetectSelectionIntent(
        HashSet<string> promptTokens,
        bool hasSpecificTokens,
        bool hasRequestedMarketSlices,
        int genericRequestedCount,
        bool isRolloverRequest)
    {
        if (promptTokens.Contains("yesterday"))
        {
            return new SelectionIntent(DateScope.Yesterday, BookableOnly: false, DisplayLabel: "Yesterday");
        }

        var asksForSettlementReview = promptTokens.Overlaps(SettlementTokens) ||
                                      promptTokens.Contains("score") ||
                                      promptTokens.Contains("scores");

        if (asksForSettlementReview)
        {
            return new SelectionIntent(DateScope.RecentFinished, BookableOnly: false, DisplayLabel: "Recent finished");
        }

        var genericRecommendationRequest = !hasSpecificTokens ||
                                           hasRequestedMarketSlices ||
                                           genericRequestedCount > 0 ||
                                           isRolloverRequest ||
                                           promptTokens.Overlaps(RecommendationTokens);

        if (genericRecommendationRequest)
        {
            return new SelectionIntent(DateScope.Today, BookableOnly: true, DisplayLabel: "Today's bookable card");
        }

        if (promptTokens.Contains("today"))
        {
            return new SelectionIntent(DateScope.Today, BookableOnly: false, DisplayLabel: "Today");
        }

        return new SelectionIntent(DateScope.RecentWindow, BookableOnly: false, DisplayLabel: "Recent card");
    }

    private static bool MatchesSelectionIntent(AiChatContextCandidate candidate, SelectionIntent intent, DateOnly todayLocalDate)
    {
        var yesterday = todayLocalDate.AddDays(-1);

        if (intent.BookableOnly && !candidate.CanBook)
        {
            return false;
        }

        return intent.Scope switch
        {
            DateScope.Today => candidate.MatchLocalDate == todayLocalDate,
            DateScope.Yesterday => candidate.MatchLocalDate == yesterday,
            DateScope.RecentFinished => candidate.MatchState == "Finished",
            _ => true
        };
    }

    private static int ResolveSelectionLimit(int limit, int requestedCandidateCount)
    {
        if (requestedCandidateCount <= 0)
        {
            return limit;
        }

        return Math.Min(Math.Max(requestedCandidateCount, 1), Math.Min(limit, MaxRequestedCandidates));
    }

    private static HashSet<string> DetectMarketFilters(HashSet<string> promptTokens)
    {
        var filters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (promptTokens.Contains("btts") || (promptTokens.Contains("both") && promptTokens.Contains("score")))
        {
            filters.Add("BothTeamsScore");
        }

        if (promptTokens.Contains("over") ||
            promptTokens.Contains("goals") ||
            promptTokens.Any(token => token.StartsWith("over", StringComparison.OrdinalIgnoreCase)))
        {
            filters.Add("Over2.5Goals");
        }

        if (promptTokens.Contains("draw") || promptTokens.Contains("draws"))
        {
            filters.Add("Draw");
        }

        if (promptTokens.Contains("straight") ||
            promptTokens.Contains("straightwin") ||
            promptTokens.Contains("straightwins") ||
            promptTokens.Contains("win") ||
            promptTokens.Contains("home") ||
            promptTokens.Contains("away") ||
            promptTokens.Contains("1x2"))
        {
            filters.Add("StraightWin");
        }

        return filters;
    }

    private static bool ShouldTreatPromptAsGenericMarketRequest(
        HashSet<string> specificTokens,
        HashSet<string> promptTokens,
        HashSet<string> marketFilters,
        IReadOnlyList<RequestedMarketSlice> requestedMarketSlices,
        int genericRequestedCount,
        bool isRolloverRequest)
    {
        if (specificTokens.Count > 0)
        {
            return false;
        }

        return isRolloverRequest ||
               requestedMarketSlices.Count > 0 ||
               genericRequestedCount > 0 ||
               marketFilters.Count > 0 ||
               promptTokens.Overlaps(RecommendationTokens);
    }

    private static IReadOnlyList<RequestedMarketSlice> ExtractRequestedMarketSlices(string userPrompt)
    {
        if (string.IsNullOrWhiteSpace(userPrompt))
        {
            return [];
        }

        var slices = new List<RequestedMarketSlice>();
        var seenByCategory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in RequestedMarketSliceRegex().Matches(userPrompt))
        {
            if (!int.TryParse(match.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count <= 0)
            {
                continue;
            }

            var category = NormalizeRequestedMarket(match.Groups["market"].Value);
            if (category is null)
            {
                continue;
            }

            var cappedCount = Math.Min(count, MaxRequestedCandidates);
            if (seenByCategory.TryGetValue(category, out var existingIndex))
            {
                var existing = slices[existingIndex];
                slices[existingIndex] = existing with
                {
                    Count = Math.Min(existing.Count + cappedCount, MaxRequestedCandidates)
                };
                continue;
            }

            seenByCategory[category] = slices.Count;
            slices.Add(new RequestedMarketSlice(category, cappedCount));
        }

        return slices;
    }

    private static int ExtractGenericRequestedCount(
        string userPrompt,
        IReadOnlyList<RequestedMarketSlice> requestedMarketSlices,
        HashSet<string> marketFilters,
        HashSet<string> promptTokens)
    {
        if (requestedMarketSlices.Count > 0 || string.IsNullOrWhiteSpace(userPrompt))
        {
            return 0;
        }

        var match = GenericPickCountRegex().Match(userPrompt);
        if (!match.Success)
        {
            return marketFilters.Count == 0 && promptTokens.Overlaps(RecommendationTokens)
                ? 5
                : 0;
        }

        return int.TryParse(match.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count > 0
            ? Math.Min(count, MaxRequestedCandidates)
            : 0;
    }

    private static List<RequestedMarketSlice> BuildImplicitMarketSlices(string userPrompt, HashSet<string> marketFilters)
    {
        var orderedMarkets = GetOrderedMentionedMarkets(userPrompt)
            .Where(market => marketFilters.Contains(market))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        if (orderedMarkets.Count == 0)
        {
            orderedMarkets = marketFilters
                .Take(3)
                .ToList();
        }

        return orderedMarkets
            .Select(market => new RequestedMarketSlice(market, 2))
            .ToList();
    }

    private static IEnumerable<string> GetOrderedMentionedMarkets(string userPrompt)
    {
        var matches = new List<(string Market, int Index)>();
        AddMarketMention(matches, userPrompt, "btts", "BothTeamsScore");
        AddMarketMention(matches, userPrompt, "both teams to score", "BothTeamsScore");
        AddMarketMention(matches, userPrompt, "over 2.5", "Over2.5Goals");
        AddMarketMention(matches, userPrompt, "over2.5", "Over2.5Goals");
        AddMarketMention(matches, userPrompt, "draw", "Draw");
        AddMarketMention(matches, userPrompt, "straight win", "StraightWin");
        AddMarketMention(matches, userPrompt, "straightwin", "StraightWin");
        AddMarketMention(matches, userPrompt, "1x2", "StraightWin");

        return matches
            .OrderBy(match => match.Index)
            .Select(match => match.Market);
    }

    private static void AddMarketMention(List<(string Market, int Index)> mentions, string userPrompt, string needle, string market)
    {
        var index = userPrompt.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            mentions.Add((market, index));
        }
    }

    private static bool ContainsSecuritySensitiveTopic(string prompt)
    {
        return prompt.Contains("password", StringComparison.Ordinal) ||
               prompt.Contains("api key", StringComparison.Ordinal) ||
               prompt.Contains("apikey", StringComparison.Ordinal) ||
               prompt.Contains("groq key", StringComparison.Ordinal) ||
               prompt.Contains("connection string", StringComparison.Ordinal) ||
               prompt.Contains("admin credential", StringComparison.Ordinal) ||
               prompt.Contains("admin password", StringComparison.Ordinal) ||
               prompt.Contains("hangfire password", StringComparison.Ordinal) ||
               prompt.Contains("secret", StringComparison.Ordinal) ||
               prompt.Contains("token", StringComparison.Ordinal) ||
               prompt.Contains("env var", StringComparison.Ordinal) ||
               prompt.Contains("environment variable", StringComparison.Ordinal);
    }

    private static bool IsWorkingSlipRefinementPrompt(string prompt, bool hasWorkingSlip)
    {
        if (!hasWorkingSlip)
        {
            return false;
        }

        return prompt.Contains("riskiest", StringComparison.Ordinal) ||
               prompt.Contains("weakest", StringComparison.Ordinal) ||
               prompt.Contains("remove", StringComparison.Ordinal) ||
               prompt.Contains("swap", StringComparison.Ordinal) ||
               prompt.Contains("replace", StringComparison.Ordinal) ||
               prompt.Contains("make it safer", StringComparison.Ordinal) ||
               prompt.Contains("make them safer", StringComparison.Ordinal) ||
               prompt.Contains("safer", StringComparison.Ordinal) && (prompt.Contains("these", StringComparison.Ordinal) || prompt.Contains("them", StringComparison.Ordinal)) ||
               prompt.Contains("from these", StringComparison.Ordinal) ||
               prompt.Contains("from them", StringComparison.Ordinal);
    }

    private static bool IsSettlementPrompt(string prompt, bool hasContextCandidates)
    {
        return SettlementTokens.Any(token => prompt.Contains(token, StringComparison.Ordinal)) &&
               (hasContextCandidates ||
                prompt.Contains("this", StringComparison.Ordinal) ||
                prompt.Contains("that", StringComparison.Ordinal) ||
                prompt.Contains("these", StringComparison.Ordinal) ||
                prompt.Contains("them", StringComparison.Ordinal));
    }

    private static bool IsAppHelpPrompt(string prompt, HashSet<string> promptTokens)
    {
        var asksForExplanation =
            prompt.Contains("what does", StringComparison.Ordinal) ||
            prompt.Contains("what is", StringComparison.Ordinal) ||
            prompt.Contains("how does", StringComparison.Ordinal) ||
            prompt.Contains("how are", StringComparison.Ordinal) ||
            prompt.Contains("how is", StringComparison.Ordinal) ||
            prompt.Contains("why is", StringComparison.Ordinal) ||
            prompt.Contains("why isn't", StringComparison.Ordinal) ||
            prompt.Contains("why isnt", StringComparison.Ordinal) ||
            prompt.Contains("explain", StringComparison.Ordinal);

        return asksForExplanation &&
               promptTokens.Overlaps(AppHelpTokens) &&
               !prompt.Contains("tell me about", StringComparison.Ordinal);
    }

    private static bool IsMatchDiscussionPrompt(string prompt, bool hasWorkingSlip, bool hasContextCandidates)
    {
        return prompt.Contains("tell me about", StringComparison.Ordinal) ||
               prompt.Contains("explain these", StringComparison.Ordinal) ||
               prompt.Contains("explain this", StringComparison.Ordinal) ||
               prompt.Contains("talk about", StringComparison.Ordinal) ||
               prompt.Contains("discuss", StringComparison.Ordinal) ||
               (hasWorkingSlip && (prompt.Contains("these matches", StringComparison.Ordinal) || prompt.Contains("them", StringComparison.Ordinal))) ||
               (hasContextCandidates && prompt.Contains("this match", StringComparison.Ordinal));
    }

    private static string? NormalizeRequestedMarket(string rawMarket)
    {
        var normalized = rawMarket.Trim().ToLowerInvariant().Replace(" ", string.Empty);

        if (normalized.Contains("btts") || normalized.Contains("bothteams"))
        {
            return "BothTeamsScore";
        }

        if (normalized.Contains("over"))
        {
            return "Over2.5Goals";
        }

        if (normalized.Contains("draw"))
        {
            return "Draw";
        }

        if (normalized.Contains("straightwin") ||
            normalized.Contains("straightwins") ||
            normalized.Contains("1x2") ||
            normalized.Contains("homewin") ||
            normalized.Contains("awaywin"))
        {
            return "StraightWin";
        }

        return null;
    }

    private static List<AiChatContextCandidate> SelectRequestedMarketSlices(
        IReadOnlyList<RankedCandidate> orderedRanked,
        IReadOnlyList<RequestedMarketSlice> requestedMarketSlices,
        int limit)
    {
        var selected = new List<AiChatContextCandidate>();
        var seenActionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var maxCandidates = Math.Min(
            MaxRequestedCandidates,
            Math.Max(limit, requestedMarketSlices.Sum(slice => slice.Count)));

        foreach (var slice in requestedMarketSlices)
        {
            foreach (var candidate in orderedRanked
                         .Where(item => item.Candidate.PredictionCategory == slice.PredictionCategory)
                         .Select(item => item.Candidate))
            {
                if (selected.Count >= maxCandidates)
                {
                    return selected;
                }

                if (!seenActionKeys.Add(candidate.ActionKey))
                {
                    continue;
                }

                selected.Add(candidate);
                if (selected.Count(candidateItem => candidateItem.PredictionCategory == slice.PredictionCategory) >= slice.Count)
                {
                    break;
                }
            }
        }

        return selected;
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

    [GeneratedRegex(
        "(?<count>\\d{1,3})\\s*(?<market>btts|both teams to score|both teams score|over\\s*2(?:\\.|,)?5|over2(?:\\.|,)?5|over|straight\\s*wins?|straightwins?|straightwin|1x2|home\\s*wins?|away\\s*wins?|draws?|draw)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex RequestedMarketSliceRegex();

    [GeneratedRegex("(?<count>\\d{1,3})\\s*(?:strong|safe|safer|best|top)?\\s*(?:pick|picks|game|games|match|matches|leg|legs)\\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex GenericPickCountRegex();

    [GeneratedRegex("roll\\s*over|rollover", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex RolloverIntentRegex();

    [GeneratedRegex("(?:roll\\s*over|rollover)(?:\\s*(?:of|to|target|around|about))?\\s*(?<odds>\\d{1,3}(?:[\\.,]\\d{1,2})?)\\s*odds?", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex RolloverTargetOddsRegex();

    [GeneratedRegex("\\b(?<odds>\\d{1,3}(?:[\\.,]\\d{1,2})?)\\s*odds?\\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex GenericOddsRegex();

    [GeneratedRegex("^\\s*(?<odds>\\d{1,3}(?:[\\.,]\\d{1,2})?)\\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BareOddsRegex();

    public sealed class AiChatContextSelection
    {
        public IReadOnlyList<AiChatContextCandidate> Candidates { get; init; } = [];
        public int TotalAvailableCount { get; init; }
        public bool NoRelevantMatchesFound { get; init; }
        public IReadOnlyList<RequestedMarketSlice> RequestedMarketSlices { get; init; } = [];
        public int RequestedCandidateCount { get; init; }
        public bool IsRolloverRequest { get; init; }
        public double? RequestedCombinedOdds { get; init; }
        public bool NeedsRolloverTargetOdds { get; init; }
        public string DateScopeLabel { get; init; } = "Current card";
    }

    public sealed class AiChatContextCandidate
    {
        public string ActionKey { get; init; } = string.Empty;
        public int PredictionId { get; init; }
        public DateOnly MatchLocalDate { get; init; }
        public string League { get; init; } = string.Empty;
        public string KickoffTime { get; init; } = string.Empty;
        public string HomeTeam { get; init; } = string.Empty;
        public string AwayTeam { get; init; } = string.Empty;
        public string PredictionCategory { get; init; } = string.Empty;
        public string PredictedOutcome { get; init; } = string.Empty;
        public string? ActualScore { get; init; }
        public string? ActualOutcome { get; init; }
        public decimal? ConfidenceScore { get; init; }
        public decimal? RawConfidenceScore { get; init; }
        public double ThresholdUsed { get; init; }
        public string ThresholdSource { get; init; } = string.Empty;
        public string CalibratorUsed { get; init; } = string.Empty;
        public bool WasPublished { get; init; }
        public string MatchState { get; init; } = "Upcoming";
        public bool CanBook { get; init; }
        public double MarginAboveThreshold { get; init; }
        public double? MarketProbability { get; init; }
        public double? EstimatedOdds { get; init; }
        public double? EdgePoints { get; init; }
        internal HashSet<string> SearchTokens { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        internal string FixtureKey { get; init; } = string.Empty;
    }

    public sealed class AiChatCandidatePricing
    {
        public double? MarketProbability { get; init; }
        public double? EstimatedDecimalOdds { get; init; }
    }

    public sealed record RequestedMarketSlice(string PredictionCategory, int Count)
    {
        public string DisplayName => PredictionCategory switch
        {
            "BothTeamsScore" => "BTTS",
            "Over2.5Goals" => "Over 2.5",
            "Draw" => "Draw",
            "StraightWin" => "Straight Win",
            _ => PredictionCategory
        };
    }

    private sealed record RankedCandidate(AiChatContextCandidate Candidate, double Score, int EntityMatchCount)
    {
    }

    private sealed record SelectionIntent(DateScope Scope, bool BookableOnly, string DisplayLabel);

    private enum DateScope
    {
        Today,
        Yesterday,
        RecentFinished,
        RecentWindow
    }
}
