using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
        "and", "asked", "asking", "away", "banker", "bankers", "best", "bet", "bets", "book", "booking", "both", "btts", "can", "card", "chat",
        "clear", "clears", "combo", "combination", "currently", "day", "days", "doing", "draw", "for", "from", "game", "games", "give", "goals", "good", "help", "home", "i", "in", "into", "is",
        "it", "just", "leg", "legs", "list", "listed", "make", "marked", "match", "matches", "me", "more", "need", "needed", "odd", "odds", "of", "on", "ones", "only", "open", "over", "pick", "picks", "please",
        "prediction", "predictions", "rather", "recent", "recommend", "recommended", "recommending", "recommendation", "recommendations", "result", "results", "safe", "safer", "said", "score", "settle", "settled", "show", "slip", "some", "still", "straight", "strong",
        "straightwin", "straightwins", "stronger", "rollover", "supplied", "teams", "than", "the", "them", "these", "this", "those", "ticket", "to",
        "today", "top", "total", "totals", "altogether", "under", "value", "why", "won", "yesterday",
        "want", "wanted", "wanting", "what", "which", "win", "wins", "with", "would", "you", "your", "red", "green", "finished", "lost", "landed", "did", "mix", "mixture", "suggest", "suggested", "random", "randomly",
        "explain", "explained", "discuss", "discussion", "talk", "riskiest", "weakest", "remove", "swap", "replace", "fits", "left", "also", "well", "same", "fixture", "fixtures", "available"
    };

    /// <summary>
    /// Long generic words that users often misspell; near-misses must not become team/league entity filters.
    /// </summary>
    private static readonly string[] TypoProneGenericTokens =
    [
        "prediction", "predictions", "recommendation", "recommendations",
        "fixture", "fixtures", "matches", "listed", "marked", "available", "currently"
    ];

    private static readonly HashSet<string> RecommendationTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "best", "safe", "safer", "strong", "stronger", "top", "pick", "picks", "list", "show", "give", "recommend", "recommended", "recommendation", "recommendations", "suggest", "suggested", "mix", "mixture", "combo", "combination", "random", "randomly"
    };

    private static readonly HashSet<string> SettlementTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "finished", "settle", "settled", "result", "results", "red", "green", "won", "lost", "landed"
    };

    private static readonly HashSet<string> AppHelpTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "analytics", "analytic", "brier", "reliability", "resolution", "uncertainty", "threshold", "thresholds",
        "calibrator", "calibration", "value", "pricing", "freshness", "edge", "exclusion", "excluded", "source",
        "chip", "chips", "live", "upcoming", "finished", "selected", "selection", "meaning", "mean", "ev", "clv",
        "expected", "mispriced", "closing"
    };

    public static AiChatContextSelection BuildSelection(
        IEnumerable<Prediction> predictions,
        string userPrompt,
        DateTime nowUtc,
        IReadOnlyDictionary<int, AiChatCandidatePricing>? pricingByPredictionId = null,
        int limit = 40)
    {
        var parsed = AiChatRequestParser.ParseDeterministic(userPrompt, null, false, false);
        return BuildSelection(predictions, parsed.Request, nowUtc, pricingByPredictionId, limit);
    }

    public static AiChatContextSelection BuildSelection(
        IEnumerable<Prediction> predictions,
        AiChatNormalizedRequest request,
        DateTime nowUtc,
        IReadOnlyDictionary<int, AiChatCandidatePricing>? pricingByPredictionId = null,
        int limit = 40)
    {
        var nowLocal = DateTimeProvider.ConvertUtcToLocal(nowUtc);
        var todayLocalDate = DateOnly.FromDateTime(nowLocal);
        var candidates = BuildCandidateCatalog(predictions, nowUtc, pricingByPredictionId);

        if (candidates.Count == 0)
        {
            return new AiChatContextSelection
            {
                Candidates = [],
                TotalAvailableCount = 0,
                NormalizedRequest = request
            };
        }

        var selectionIntent = DetectSelectionIntent(request);
        var marketFilters = request.RequestedMarkets
            .Select(market => market.PredictionCategory)
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entityTerms = ShouldApplyNamedFixtureFilter(request)
            ? request.EntityTerms.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var requestedCandidateCount = ResolveRequestedCandidateCount(request);

        var ranked = candidates
            .Where(candidate => MatchesSelectionIntent(candidate, selectionIntent, todayLocalDate, request.BookableOnly))
            .Select(candidate => CreateRankedCandidate(candidate, request, marketFilters, entityTerms, selectionIntent, todayLocalDate))
            .ToList();

        if (entityTerms.Count > 0)
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
                    RequestedMarketSlices = BuildRequestedMarketSlices(request),
                    RequestedCandidateCount = requestedCandidateCount,
                    IsRolloverRequest = !string.IsNullOrWhiteSpace(request.ActionDirective) && request.ActionDirective == "target_odds",
                    RequestedCombinedOdds = request.TargetCombinedOdds,
                    NeedsRolloverTargetOdds = request.ActionDirective == "target_odds" && !request.TargetCombinedOdds.HasValue,
                    DateScopeLabel = selectionIntent.DisplayLabel,
                    NormalizedRequest = request,
                    InterpretationNotes = request.InterpretationNotes
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

        var orderedRanked = OrderRankedCandidates(ranked, request, nowUtc);

        var selectionOutcome = request.RequireSameFixtureMarkets
            ? SelectSameFixtureMarketCandidates(orderedRanked, request, limit)
            : SelectRequestedCandidates(orderedRanked, request, limit);

        return new AiChatContextSelection
        {
            Candidates = selectionOutcome.Candidates,
            TotalAvailableCount = candidates.Count,
            NoRelevantMatchesFound = selectionOutcome.Candidates.Count == 0 &&
                                     (entityTerms.Count > 0 || request.RequireSameFixtureMarkets),
            RequestedMarketSlices = selectionOutcome.RequestedSlices,
            RequestedCandidateCount = requestedCandidateCount,
            IsRolloverRequest = request.ActionDirective == "target_odds",
            RequestedCombinedOdds = request.TargetCombinedOdds,
            NeedsRolloverTargetOdds = request.ActionDirective == "target_odds" && !request.TargetCombinedOdds.HasValue,
            DateScopeLabel = selectionIntent.DisplayLabel,
            NormalizedRequest = request,
            ResolvedMarketMix = selectionOutcome.ResolvedMarketMix,
            ShortfallWarnings = selectionOutcome.ShortfallWarnings,
            InterpretationNotes = request.InterpretationNotes
        };
    }

    public static string CreateActionKey(Prediction prediction) => $"P{prediction.Id}";

    public static IReadOnlyList<AiChatContextCandidate> BuildCandidateCatalog(
        IEnumerable<Prediction> predictions,
        DateTime nowUtc,
        IReadOnlyDictionary<int, AiChatCandidatePricing>? pricingByPredictionId = null,
        IReadOnlyDictionary<int, string>? featureContributionsByPredictionId = null)
    {
        var todayLocalDate = DateOnly.FromDateTime(DateTimeProvider.ConvertUtcToLocal(nowUtc));
        var recentStartDate = todayLocalDate.AddDays(-7);

        return predictions
            .Where(prediction => prediction.IsCurrentRevision && prediction.WasPublished)
            .Where(prediction => prediction.MatchLocalDate >= recentStartDate && prediction.MatchLocalDate <= todayLocalDate)
            .Select(prediction => CreateCandidate(
                prediction,
                pricingByPredictionId?.GetValueOrDefault(prediction.Id),
                featureContributionsByPredictionId?.GetValueOrDefault(prediction.Id),
                nowUtc,
                todayLocalDate))
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
            "Under2.5Goals" => "Under2.5",
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
        string? featureContributionsJson,
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

        var signalBreakdown = MatchPredictor.Infrastructure.Statistics.SignalBreakdownParser.TryParse(
            featureContributionsJson,
            prediction.PredictionCategory,
            prediction.PredictedOutcome,
            modelProbability);

        return new AiChatContextCandidate
        {
            ActionKey = CreateActionKey(prediction),
            PredictionId = prediction.Id,
            MatchLocalDate = prediction.MatchLocalDate,
            MatchDateTimeUtc = prediction.MatchDateTime,
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
            FeatureContributionsJson = featureContributionsJson,
            SignalBreakdown = signalBreakdown,
            FixtureKey = $"{prediction.League}|{prediction.HomeTeam}|{prediction.AwayTeam}|{prediction.Time}",
            SearchTokens = Tokenize(searchableText)
        };
    }

    private static RankedCandidate CreateRankedCandidate(
        AiChatContextCandidate candidate,
        AiChatNormalizedRequest request,
        HashSet<string> marketFilters,
        HashSet<string> entityTerms,
        SelectionIntent selectionIntent,
        DateOnly todayLocalDate)
    {
        var entityMatches = candidate.SearchTokens.Intersect(entityTerms, StringComparer.OrdinalIgnoreCase).Count();
        var score = CalculateRankingScore(candidate, request, marketFilters, selectionIntent, todayLocalDate, entityMatches);

        return new RankedCandidate(candidate, score, entityMatches);
    }

    private static double CalculateRankingScore(
        AiChatContextCandidate candidate,
        AiChatNormalizedRequest request,
        HashSet<string> marketFilters,
        SelectionIntent selectionIntent,
        DateOnly todayLocalDate,
        int entityMatches)
    {
        var score = ComputeBlendedCoreStrength(candidate) * 100d;
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

        if (string.Equals(request.SafetyBias, "safer", StringComparison.OrdinalIgnoreCase))
        {
            score += candidate.PredictionCategory == "StraightWin" ? 20d : 0d;
            score -= candidate.PredictionCategory == "Draw" ? 10d : 0d;

            if (candidate.EstimatedOdds is > 0)
            {
                score += candidate.EstimatedOdds <= 1.65 ? 12d : Math.Max(-18d, 12d - ((candidate.EstimatedOdds.Value - 1.65d) * 20d));
            }
        }

        if (request.ValueBias)
        {
            score += candidate.MarginAboveThreshold * 120d;
        }

        if (request.TargetCombinedOdds.HasValue && candidate.EstimatedOdds is > 0)
        {
            score += candidate.EstimatedOdds <= 1.85 ? 8d : 4d;
        }

        score += entityMatches * 40d;
        return score;
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

    internal static double ComputeAppCoreStrength(AiChatContextCandidate candidate)
    {
        var confidence = Math.Clamp((double)(candidate.ConfidenceScore ?? candidate.RawConfidenceScore ?? decimal.Zero), 0d, 1d);
        var marginStrength = Math.Clamp(candidate.MarginAboveThreshold / 0.18d, 0d, 1d);
        var edgeStrength = candidate.EdgePoints.HasValue
            ? Math.Clamp((candidate.EdgePoints.Value + 2d) / 12d, 0d, 1d)
            : 0.5d;

        return (confidence * 0.55d) + (marginStrength * 0.25d) + (edgeStrength * 0.20d);
    }

    internal static double ComputeBlendedCoreStrength(AiChatContextCandidate candidate)
    {
        var appStrength = ComputeAppCoreStrength(candidate);
        if (!candidate.FootballSupportScore.HasValue)
        {
            return appStrength;
        }

        var footballStrength = NormalizeFootballSupportScore(candidate.FootballSupportScore.Value);
        return (appStrength * 0.75d) + (footballStrength * 0.25d);
    }

    internal static double NormalizeFootballSupportScore(double supportScore)
    {
        return Math.Clamp((supportScore + 1d) / 2d, 0d, 1d);
    }

    private static SelectionIntent DetectSelectionIntent(AiChatNormalizedRequest request)
    {
        if (string.Equals(request.Scope, "yesterday", StringComparison.OrdinalIgnoreCase))
        {
            return new SelectionIntent(DateScope.Yesterday, BookableOnly: false, DisplayLabel: "Yesterday");
        }

        if (string.Equals(request.Scope, "recent_finished", StringComparison.OrdinalIgnoreCase))
        {
            return new SelectionIntent(DateScope.RecentFinished, BookableOnly: false, DisplayLabel: "Recent finished");
        }

        if (string.Equals(request.Scope, "today", StringComparison.OrdinalIgnoreCase))
        {
            return new SelectionIntent(
                DateScope.Today,
                BookableOnly: request.BookableOnly,
                DisplayLabel: request.BookableOnly ? "Today's bookable card" : "Today");
        }

        return new SelectionIntent(DateScope.RecentWindow, BookableOnly: false, DisplayLabel: "Recent card");
    }

    private static bool MatchesSelectionIntent(AiChatContextCandidate candidate, SelectionIntent intent, DateOnly todayLocalDate, bool requestBookableOnly)
    {
        var yesterday = todayLocalDate.AddDays(-1);

        if ((intent.BookableOnly || requestBookableOnly) && !candidate.CanBook)
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

    private static bool ShouldApplyNamedFixtureFilter(AiChatNormalizedRequest request)
    {
        return request.Intent == AiChatIntent.MatchDiscussion && request.EntityTerms.Count > 0;
    }

    private static int ResolveRequestedCandidateCount(AiChatNormalizedRequest request)
    {
        if (request.RequestedTotalCount.HasValue)
        {
            return request.RequestedTotalCount.Value;
        }

        var explicitCount = request.RequestedMarkets
            .Where(market => market.Count.HasValue)
            .Sum(market => market.Count!.Value);
        if (explicitCount > 0)
        {
            return explicitCount;
        }

        if (request.RequestedMarkets.Count > 0)
        {
            return Math.Min(6, request.RequestedMarkets.Count * 2);
        }

        return request.Intent is AiChatIntent.RecommendPicks or AiChatIntent.MixedMarketRecommendation or AiChatIntent.ValueBetRequest
            ? 5
            : 0;
    }

    private static HashSet<string> DetectMarketFilters(HashSet<string> promptTokens)
    {
        var filters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (promptTokens.Contains("btts") ||
            promptTokens.Contains("goalgoal") ||
            (promptTokens.Contains("both") && promptTokens.Contains("score")))
        {
            filters.Add("BothTeamsScore");
        }

        if (promptTokens.Contains("over") ||
            promptTokens.Contains("goals") ||
            promptTokens.Any(token => token.StartsWith("over", StringComparison.OrdinalIgnoreCase)))
        {
            filters.Add("Over2.5Goals");
        }

        if (promptTokens.Contains("under") ||
            promptTokens.Any(token => token.StartsWith("under", StringComparison.OrdinalIgnoreCase)))
        {
            filters.Add("Under2.5Goals");
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
            match = TotalPickCountRegex().Match(userPrompt);
        }

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

    private static List<RequestedMarketSlice> BuildImplicitMarketSlices(
        string userPrompt,
        HashSet<string> marketFilters,
        int totalRequestedCount)
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

        if (orderedMarkets.Count == 0)
        {
            return [];
        }

        if (totalRequestedCount <= 0)
        {
            return orderedMarkets
                .Select(market => new RequestedMarketSlice(market, 2))
                .ToList();
        }

        var boundedTotal = Math.Min(totalRequestedCount, MaxRequestedCandidates);
        var baseCount = boundedTotal / orderedMarkets.Count;
        var remainder = boundedTotal % orderedMarkets.Count;
        var slices = new List<RequestedMarketSlice>(orderedMarkets.Count);

        for (var index = 0; index < orderedMarkets.Count; index++)
        {
            var count = baseCount + (index < remainder ? 1 : 0);
            if (count <= 0)
            {
                continue;
            }

            slices.Add(new RequestedMarketSlice(orderedMarkets[index], count));
        }

        return slices;
    }

    private static IEnumerable<string> GetOrderedMentionedMarkets(string userPrompt)
    {
        var matches = new List<(string Market, int Index)>();
        AddMarketMention(matches, userPrompt, "btts", "BothTeamsScore");
        AddMarketMention(matches, userPrompt, "gg", "BothTeamsScore");
        AddMarketMention(matches, userPrompt, "goalgoal", "BothTeamsScore");
        AddMarketMention(matches, userPrompt, "goal goal", "BothTeamsScore");
        AddMarketMention(matches, userPrompt, "both teams to score", "BothTeamsScore");
        AddMarketMention(matches, userPrompt, "over 2.5", "Over2.5Goals");
        AddMarketMention(matches, userPrompt, "over2.5", "Over2.5Goals");
        AddMarketMention(matches, userPrompt, "under 2.5", "Under2.5Goals");
        AddMarketMention(matches, userPrompt, "under2.5", "Under2.5Goals");
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

    internal static bool ContainsSecuritySensitiveTopic(string prompt)
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

    internal static bool IsWorkingSlipRefinementPrompt(string prompt, bool hasWorkingSlip)
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

    internal static bool IsSettlementPrompt(string prompt, bool hasContextCandidates)
    {
        return SettlementTokens.Any(token => prompt.Contains(token, StringComparison.Ordinal)) &&
               (hasContextCandidates ||
                prompt.Contains("this", StringComparison.Ordinal) ||
                prompt.Contains("that", StringComparison.Ordinal) ||
                prompt.Contains("these", StringComparison.Ordinal) ||
                prompt.Contains("them", StringComparison.Ordinal));
    }

    internal static bool IsAppHelpPrompt(string prompt, HashSet<string> promptTokens)
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
               (promptTokens.Overlaps(AppHelpTokens) ||
                prompt.Contains(" expected value", StringComparison.Ordinal) ||
                prompt.Contains(" ev", StringComparison.Ordinal) ||
                prompt.EndsWith("ev", StringComparison.Ordinal) ||
                prompt.Contains(" clv", StringComparison.Ordinal) ||
                prompt.EndsWith("clv", StringComparison.Ordinal)) &&
               !prompt.Contains("tell me about", StringComparison.Ordinal);
    }

    internal static bool IsMatchDiscussionPrompt(string prompt, bool hasWorkingSlip, bool hasContextCandidates)
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

        if (normalized.Contains("btts") ||
            normalized.Contains("bothteams") ||
            normalized.Contains("goalgoal") ||
            normalized == "gg")
        {
            return "BothTeamsScore";
        }

        if (normalized.Contains("over"))
        {
            return "Over2.5Goals";
        }

        if (normalized.Contains("under"))
        {
            return "Under2.5Goals";
        }

        if (normalized.Contains("draw"))
        {
            return "Draw";
        }

        if (normalized.Contains("straightwin") ||
            normalized.Contains("straightwins") ||
            normalized.Contains("1x2") ||
            normalized.Contains("homewin") ||
            normalized.Contains("awaywin") ||
            normalized == "win" ||
            normalized == "wins")
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

    private static SelectionOutcome SelectRequestedCandidates(
        IReadOnlyList<RankedCandidate> orderedRanked,
        AiChatNormalizedRequest request,
        int limit)
    {
        if (request.RequestedMarkets.Count == 0)
        {
            var maxCandidates = request.ActionDirective == "target_odds"
                ? limit
                : ResolveSelectionLimit(limit, ResolveRequestedCandidateCount(request));

            var genericSelection = orderedRanked
                .Take(maxCandidates)
                .Select(item => item.Candidate)
                .ToList();

            return new SelectionOutcome(
                genericSelection,
                [],
                BuildResolvedMarketMix(genericSelection),
                []);
        }

        var requestedSlices = BuildRequestedMarketSlices(request);
        var selected = SelectRequestedMarketSlices(orderedRanked, requestedSlices, limit);
        var shortfallWarnings = BuildShortfallWarnings(requestedSlices, selected);
        var resolvedMarketMix = BuildResolvedMarketMix(selected);

        var explicitRequestedCounts = request.RequestedMarkets.Any(market => market.ExplicitCount && market.Count.HasValue);
        var targetTotal = request.RequestedTotalCount ?? requestedSlices.Sum(slice => slice.Count);
        if (!explicitRequestedCounts &&
            request.FlexibleMix &&
            targetTotal > selected.Count)
        {
            var seenActionKeys = selected
                .Select(candidate => candidate.ActionKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var allowedMarkets = request.RequestedMarkets
                .Select(market => market.PredictionCategory)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in orderedRanked.Select(item => item.Candidate))
            {
                if (selected.Count >= targetTotal)
                {
                    break;
                }

                if (!allowedMarkets.Contains(candidate.PredictionCategory) || !seenActionKeys.Add(candidate.ActionKey))
                {
                    continue;
                }

                selected.Add(candidate);
            }

            shortfallWarnings = BuildShortfallWarnings(requestedSlices, selected);
            resolvedMarketMix = BuildResolvedMarketMix(selected);
        }

        return new SelectionOutcome(selected, requestedSlices, resolvedMarketMix, shortfallWarnings);
    }

    private static SelectionOutcome SelectSameFixtureMarketCandidates(
        IReadOnlyList<RankedCandidate> orderedRanked,
        AiChatNormalizedRequest request,
        int limit)
    {
        var requiredMarkets = request.RequestedMarkets
            .Select(market => market.PredictionCategory)
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (requiredMarkets.Count < 2)
        {
            return SelectRequestedCandidates(orderedRanked, request, limit);
        }

        var requiredMarketSet = requiredMarkets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var maxFixtures = ResolveSelectionLimit(
            Math.Max(1, limit / requiredMarkets.Count),
            request.RequestedTotalCount ?? Math.Min(10, MaxRequestedCandidates / requiredMarkets.Count));

        var fixtureGroups = orderedRanked
            .Select(item => item.Candidate)
            .Where(candidate => requiredMarketSet.Contains(candidate.PredictionCategory))
            .GroupBy(candidate => BuildSameFixtureGroupKey(candidate), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var legsByMarket = group
                    .GroupBy(candidate => candidate.PredictionCategory, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        marketGroup => marketGroup.Key,
                        marketGroup => marketGroup
                            .OrderByDescending(candidate => candidate.ConfidenceScore ?? decimal.Zero)
                            .ThenByDescending(candidate => candidate.MarginAboveThreshold)
                            .First(),
                        StringComparer.OrdinalIgnoreCase);

                if (requiredMarkets.Any(market => !legsByMarket.ContainsKey(market)))
                {
                    return null;
                }

                var selectedLegs = requiredMarkets
                    .Select(market => legsByMarket[market])
                    .ToList();
                var fixtureScore = selectedLegs.Average(candidate =>
                    (double)(candidate.ConfidenceScore ?? decimal.Zero) * 0.7d +
                    candidate.MarginAboveThreshold * 0.3d);

                return new SameFixtureSelection(selectedLegs, fixtureScore);
            })
            .Where(group => group is not null)
            .Select(group => group!)
            .OrderByDescending(group => group.Score)
            .Take(maxFixtures)
            .ToList();

        var selected = fixtureGroups
            .SelectMany(group => group.Legs)
            .ToList();

        var marketNames = string.Join(" + ", requiredMarkets.Select(GetMarketDisplayName));
        var shortfallWarnings = selected.Count == 0
            ?
            [
                $"No fixtures in the current card are published under both {marketNames}."
            ]
            : new List<string>();

        var requestedSlices = requiredMarkets
            .Select(market => new RequestedMarketSlice(
                market,
                fixtureGroups.Count))
            .ToList();

        return new SelectionOutcome(
            selected,
            requestedSlices,
            BuildResolvedMarketMix(selected),
            shortfallWarnings);
    }

    private static string BuildSameFixtureGroupKey(AiChatContextCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.FixtureKey))
        {
            return $"{candidate.MatchLocalDate:yyyy-MM-dd}|{candidate.FixtureKey}";
        }

        return string.Join(
            "|",
            candidate.MatchLocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            candidate.League.Trim().ToLowerInvariant(),
            candidate.HomeTeam.Trim().ToLowerInvariant(),
            candidate.AwayTeam.Trim().ToLowerInvariant(),
            candidate.KickoffTime.Trim().ToLowerInvariant());
    }

    private static string GetMarketDisplayName(string predictionCategory) =>
        predictionCategory switch
        {
            "BothTeamsScore" => "BTTS",
            "Over2.5Goals" => "Over 2.5",
            "Under2.5Goals" => "Under 2.5",
            "Draw" => "Draw",
            "StraightWin" => "Straight Win",
            _ => predictionCategory
        };

    private static List<RankedCandidate> OrderRankedCandidates(
        IReadOnlyList<RankedCandidate> ranked,
        AiChatNormalizedRequest request,
        DateTime nowUtc)
    {
        if (request.RandomSelection)
        {
            var promptSeed = string.IsNullOrWhiteSpace(request.RawPrompt) ? "random-selection" : request.RawPrompt;
            var seedText = $"{promptSeed}|{nowUtc.ToString("O", CultureInfo.InvariantCulture)}";
            return ranked
                .OrderBy(item => ComputeStableRandomOrderKey(seedText, item.Candidate.ActionKey))
                .ThenBy(item => item.Candidate.ActionKey, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return ranked
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Candidate.ConfidenceScore ?? decimal.Zero)
            .ThenByDescending(item => item.Candidate.EdgePoints ?? double.MinValue)
            .ToList();
    }

    public static IReadOnlyList<AiChatContextCandidate> ReorderCandidates(
        IReadOnlyList<AiChatContextCandidate> candidates,
        AiChatNormalizedRequest request,
        DateTime nowUtc)
    {
        var todayLocalDate = DateOnly.FromDateTime(DateTimeProvider.ConvertUtcToLocal(nowUtc));
        var selectionIntent = DetectSelectionIntent(request);
        var marketFilters = request.RequestedMarkets
            .Select(market => market.PredictionCategory)
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entityTerms = ShouldApplyNamedFixtureFilter(request)
            ? request.EntityTerms.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var ranked = candidates
            .Select(candidate =>
            {
                var entityMatches = candidate.SearchTokens.Intersect(entityTerms, StringComparer.OrdinalIgnoreCase).Count();
                var score = CalculateRankingScore(candidate, request, marketFilters, selectionIntent, todayLocalDate, entityMatches);
                return new RankedCandidate(candidate, score, entityMatches);
            })
            .ToList();

        return OrderRankedCandidates(ranked, request, nowUtc)
            .Select(item => item.Candidate)
            .ToList();
    }

    private static ulong ComputeStableRandomOrderKey(string seedText, string actionKey)
    {
        var combinedBytes = Encoding.UTF8.GetBytes($"{seedText}\n{actionKey}");
        var hashBytes = SHA256.HashData(combinedBytes);
        return BitConverter.ToUInt64(hashBytes, 0);
    }

    private static List<RequestedMarketSlice> BuildRequestedMarketSlices(AiChatNormalizedRequest request)
    {
        if (request.RequestedMarkets.Count == 0)
        {
            return [];
        }

        if (request.RequestedMarkets.Any(market => market.Count.HasValue))
        {
            return request.RequestedMarkets
                .Select(market => new RequestedMarketSlice(market.PredictionCategory, market.Count ?? 0))
                .Where(slice => slice.Count > 0)
                .ToList();
        }

        var targetTotal = request.RequestedTotalCount ?? Math.Min(6, request.RequestedMarkets.Count * 2);
        if (targetTotal <= 0)
        {
            return [];
        }

        var boundedTotal = Math.Min(targetTotal, MaxRequestedCandidates);
        var orderedMarkets = request.RequestedMarkets
            .Select(market => market.PredictionCategory)
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (orderedMarkets.Count == 0)
        {
            return [];
        }

        var baseCount = boundedTotal / orderedMarkets.Count;
        var remainder = boundedTotal % orderedMarkets.Count;
        var slices = new List<RequestedMarketSlice>(orderedMarkets.Count);
        for (var index = 0; index < orderedMarkets.Count; index++)
        {
            var count = baseCount + (index < remainder ? 1 : 0);
            if (count <= 0)
            {
                continue;
            }

            slices.Add(new RequestedMarketSlice(orderedMarkets[index], count));
        }

        return slices;
    }

    private static List<AiChatRequestedMarket> BuildResolvedMarketMix(IReadOnlyCollection<AiChatContextCandidate> selectedCandidates)
    {
        return selectedCandidates
            .GroupBy(candidate => candidate.PredictionCategory, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AiChatRequestedMarket
            {
                PredictionCategory = group.Key,
                Count = group.Count(),
                ExplicitCount = true
            })
            .OrderBy(market => market.DisplayName)
            .ToList();
    }

    private static List<string> BuildShortfallWarnings(
        IReadOnlyList<RequestedMarketSlice> requestedSlices,
        IReadOnlyCollection<AiChatContextCandidate> selectedCandidates)
    {
        var warnings = new List<string>();
        foreach (var slice in requestedSlices)
        {
            var selectedCount = selectedCandidates.Count(candidate =>
                string.Equals(candidate.PredictionCategory, slice.PredictionCategory, StringComparison.OrdinalIgnoreCase));
            if (selectedCount >= slice.Count)
            {
                continue;
            }

            warnings.Add($"Requested {slice.Count} {slice.DisplayName}, but only {selectedCount} are currently available on the published card.");
        }

        return warnings;
    }

    internal static HashSet<string> TokenizeForParsing(string value) => Tokenize(value);

    internal static List<string> ExtractSpecificTokens(string value)
    {
        return Tokenize(value)
            .Where(token => !IsGenericOrNearGenericToken(token))
            .ToList();
    }

    internal static bool IsGenericOrNearGenericToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return true;
        }

        if (GenericPromptTokens.Contains(token))
        {
            return true;
        }

        // Only fuzzy-match longer stems so short team names (e.g. Inter) stay as entities.
        if (token.Length < 6)
        {
            return false;
        }

        foreach (var generic in TypoProneGenericTokens)
        {
            if (Math.Abs(generic.Length - token.Length) > 1)
            {
                continue;
            }

            if (LevenshteinDistance(token, generic) <= 1)
            {
                return true;
            }
        }

        return false;
    }

    private static int LevenshteinDistance(string left, string right)
    {
        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
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
        "(?<count>\\d{1,3})\\s*(?<market>btts|gg|goal\\s*goal|goalgoal|both teams to score|both teams score|over\\s*2(?:\\.|,)?5|over2(?:\\.|,)?5|under\\s*2(?:\\.|,)?5|under2(?:\\.|,)?5|over|under|straight\\s*wins?|straightwins?|straightwin|wins?|1x2|home\\s*wins?|away\\s*wins?|draws?|draw)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex RequestedMarketSliceRegex();

    [GeneratedRegex("(?<count>\\d{1,3})(?:\\s+of(?:\\s+the)?)?(?:\\s+(?!(?:pick|picks|prediction|predictions|tip|tips|game|games|match|matches|leg|legs)\\b)\\w+){0,3}\\s+(?:pick|picks|prediction|predictions|tip|tips|game|games|match|matches|leg|legs)\\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex GenericPickCountRegex();

    [GeneratedRegex("\\btotal(?:\\s+of)?\\s*(?<count>\\d{1,3})\\b|\\b(?<count>\\d{1,3})\\s*total\\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TotalPickCountRegex();

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
        public AiChatNormalizedRequest? NormalizedRequest { get; init; }
        public IReadOnlyList<AiChatRequestedMarket> ResolvedMarketMix { get; init; } = [];
        public IReadOnlyList<string> ShortfallWarnings { get; init; } = [];
        public IReadOnlyList<string> InterpretationNotes { get; init; } = [];
    }

    public sealed class AiChatContextCandidate
    {
        public string ActionKey { get; init; } = string.Empty;
        public int PredictionId { get; init; }
        public DateOnly MatchLocalDate { get; init; }
        public DateTime? MatchDateTimeUtc { get; init; }
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
        public FootballMatchInsightSnapshot? FootballInsight { get; set; }
        public double? FootballSupportScore { get; set; }
        public string? FeatureContributionsJson { get; init; }
        public SignalBreakdownSnapshot? SignalBreakdown { get; init; }
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
            "Under2.5Goals" => "Under 2.5",
            "Draw" => "Draw",
            "StraightWin" => "Straight Win",
            _ => PredictionCategory
        };
    }

    private sealed record RankedCandidate(AiChatContextCandidate Candidate, double Score, int EntityMatchCount)
    {
    }

    private sealed record SameFixtureSelection(List<AiChatContextCandidate> Legs, double Score);

    private sealed record SelectionOutcome(
        List<AiChatContextCandidate> Candidates,
        List<RequestedMarketSlice> RequestedSlices,
        List<AiChatRequestedMarket> ResolvedMarketMix,
        List<string> ShortfallWarnings);

    private sealed record SelectionIntent(DateScope Scope, bool BookableOnly, string DisplayLabel);

    private enum DateScope
    {
        Today,
        Yesterday,
        RecentFinished,
        RecentWindow
    }
}
