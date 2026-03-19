using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using Microsoft.Extensions.Logging;

namespace MatchPredictor.Infrastructure.Services;

public partial class AiChatRequestParser
{
    private const int MaxRequestedCandidates = 60;

    private static readonly Dictionary<string, int> QuantityWordCounts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["couple"] = 2,
        ["few"] = 3,
        ["several"] = 4,
        ["handful"] = 5
    };

    private static readonly HashSet<string> QuantityWords = new(QuantityWordCounts.Keys, StringComparer.OrdinalIgnoreCase);

    private readonly IAiChatSchemaFallbackService _schemaFallbackService;
    private readonly ILogger<AiChatRequestParser> _logger;

    public AiChatRequestParser(
        IAiChatSchemaFallbackService schemaFallbackService,
        ILogger<AiChatRequestParser> logger)
    {
        _schemaFallbackService = schemaFallbackService;
        _logger = logger;
    }

    public async Task<AiChatParseResult> ParseAsync(
        string userPrompt,
        AiChatSessionState? sessionState,
        bool hasWorkingSlip,
        bool hasContextCandidates,
        CancellationToken ct = default)
    {
        var deterministic = ParseDeterministic(userPrompt, sessionState, hasWorkingSlip, hasContextCandidates);
        if (!deterministic.Request.NeedsSemanticFallback)
        {
            return deterministic;
        }

        try
        {
            var semanticRequest = await _schemaFallbackService.TryParseAsync(userPrompt, deterministic.Request, ct);
            if (semanticRequest is null)
            {
                return deterministic;
            }

            var sanitized = SanitizeRequest(semanticRequest, userPrompt, sessionState, deterministic.Request.Intent);
            sanitized.UsedSemanticFallback = true;
            sanitized.NeedsSemanticFallback = false;
            if (!sanitized.InterpretationNotes.Any(note => note.Contains("semantic", StringComparison.OrdinalIgnoreCase)))
            {
                sanitized.InterpretationNotes.Add("Interpreted this request through semantic fallback because the wording was looser than the deterministic parser supports.");
            }

            return new AiChatParseResult
            {
                Request = sanitized,
                UsedSemanticFallback = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AI chat semantic request fallback failed. Continuing with deterministic parse.");
            return deterministic;
        }
    }

    public static AiChatParseResult ParseDeterministic(
        string userPrompt,
        AiChatSessionState? sessionState,
        bool hasWorkingSlip,
        bool hasContextCandidates)
    {
        var prompt = (userPrompt ?? string.Empty).Trim();
        var promptLower = prompt.ToLowerInvariant();
        var referencedContextMode = ResolveReferencedContextMode(promptLower, sessionState);
        var tokens = AiChatContextBuilder.TokenizeForParsing(prompt);
        var marketMentions = DetectMarketMentions(promptLower);
        var requestedMarkets = ExtractRequestedMarkets(prompt);
        var hasExplicitMarketCounts = requestedMarkets.Any(market => market.ExplicitCount && market.Count > 0);
        var requestedTotalCount = hasExplicitMarketCounts
            ? null
            : ExtractRequestedTotalCount(prompt, tokens);
        var intent = DetectIntent(prompt, promptLower, tokens, requestedMarkets, hasWorkingSlip, hasContextCandidates);
        var wantsBooking = MentionsBookingIntent(promptLower);
        var valueBias = DetectValueBias(promptLower, intent);
        var safetyBias = DetectSafetyBias(promptLower);
        double? targetCombinedOdds = AiChatContextBuilder.TryExtractRolloverTargetOdds(prompt, out var parsedTargetOdds)
            ? parsedTargetOdds
            : null;
        var isRolloverIntent = AiChatContextBuilder.MentionsRolloverIntent(prompt);
        var scope = DetermineScope(promptLower, tokens, intent, isRolloverIntent);
        var bookableOnly = DetermineBookableOnly(intent, scope);
        var actionDirective = DetectActionDirective(promptLower, wantsBooking, isRolloverIntent, targetCombinedOdds);
        if (!hasExplicitMarketCounts &&
            requestedMarkets.Count == 0 &&
            marketMentions.Count > 0 &&
            intent is AiChatIntent.MixedMarketRecommendation or AiChatIntent.RecommendPicks)
        {
            requestedMarkets = marketMentions
                .Select(category => new AiChatRequestedMarket
                {
                    PredictionCategory = category,
                    Count = null,
                    ExplicitCount = false
                })
                .ToList();
        }

        if (!requestedTotalCount.HasValue && hasExplicitMarketCounts)
        {
            requestedTotalCount = requestedMarkets
                .Where(market => market.Count.HasValue)
                .Sum(market => market.Count!.Value);
        }

        var requestedFilters = BuildRequestedFilters(scope, bookableOnly, safetyBias, valueBias, wantsBooking);
        var interpretationNotes = BuildInterpretationNotes(promptLower, requestedMarkets, requestedTotalCount, targetCombinedOdds, scope);
        var entityTerms = ExtractEntityTerms(prompt, requestedMarkets, intent);
        var flexibleMix = DetectFlexibleMix(promptLower, requestedMarkets);
        var needsSemanticFallback = DetermineNeedsSemanticFallback(
            promptLower,
            intent,
            requestedMarkets,
            requestedTotalCount,
            entityTerms,
            targetCombinedOdds,
            hasWorkingSlip,
            hasContextCandidates);

        var request = new AiChatNormalizedRequest
        {
            RawPrompt = prompt,
            Intent = intent,
            RequestedMarkets = requestedMarkets,
            RequestedFilters = requestedFilters,
            RequestedTotalCount = requestedTotalCount,
            Scope = scope,
            BookableOnly = bookableOnly,
            WantsBooking = wantsBooking,
            TargetCombinedOdds = targetCombinedOdds,
            SafetyBias = safetyBias,
            ValueBias = valueBias,
            ReferencedContextMode = referencedContextMode,
            ActionDirective = actionDirective,
            EntityTerms = entityTerms,
            InterpretationNotes = interpretationNotes,
            NeedsSemanticFallback = needsSemanticFallback,
            FlexibleMix = flexibleMix
        };

        return new AiChatParseResult
        {
            Request = SanitizeRequest(request, prompt, sessionState, intent)
        };
    }

    public static AiChatNormalizedRequest SanitizeRequest(
        AiChatNormalizedRequest request,
        string userPrompt,
        AiChatSessionState? sessionState,
        AiChatIntent defaultIntent)
    {
        var normalized = new AiChatNormalizedRequest
        {
            RawPrompt = string.IsNullOrWhiteSpace(request.RawPrompt) ? userPrompt.Trim() : request.RawPrompt.Trim(),
            Intent = request.Intent == default ? defaultIntent : request.Intent,
            RequestedTotalCount = ClampCount(request.RequestedTotalCount),
            Scope = NormalizeScope(request.Scope),
            BookableOnly = request.BookableOnly,
            WantsBooking = request.WantsBooking,
            TargetCombinedOdds = ClampTargetOdds(request.TargetCombinedOdds),
            SafetyBias = NormalizeSafetyBias(request.SafetyBias),
            ValueBias = request.ValueBias,
            ReferencedContextMode = string.IsNullOrWhiteSpace(request.ReferencedContextMode)
                ? ResolveReferencedContextMode(userPrompt.ToLowerInvariant(), sessionState)
                : request.ReferencedContextMode.Trim(),
            ActionDirective = NormalizeActionDirective(request.ActionDirective),
            InterpretationNotes = request.InterpretationNotes
                .Where(note => !string.IsNullOrWhiteSpace(note))
                .Select(note => note.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            NeedsSemanticFallback = request.NeedsSemanticFallback,
            FlexibleMix = request.FlexibleMix,
            UsedSemanticFallback = request.UsedSemanticFallback
        };

        normalized.RequestedMarkets = request.RequestedMarkets
            .Select(market => new AiChatRequestedMarket
            {
                PredictionCategory = NormalizeMarketCategory(market.PredictionCategory) ?? string.Empty,
                Count = ClampCount(market.Count),
                ExplicitCount = market.ExplicitCount
            })
            .Where(market => !string.IsNullOrWhiteSpace(market.PredictionCategory))
            .GroupBy(market => market.PredictionCategory, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var explicitCount = group.Any(item => item.ExplicitCount);
                var summedCount = group
                    .Where(item => item.Count.HasValue)
                    .Sum(item => item.Count!.Value);

                return new AiChatRequestedMarket
                {
                    PredictionCategory = group.Key,
                    Count = summedCount > 0 ? Math.Min(summedCount, MaxRequestedCandidates) : null,
                    ExplicitCount = explicitCount
                };
            })
            .ToList();

        normalized.RequestedFilters = request.RequestedFilters
            .Where(filter => !string.IsNullOrWhiteSpace(filter.Name) && !string.IsNullOrWhiteSpace(filter.Value))
            .Select(filter => new AiChatRequestedFilter
            {
                Name = filter.Name.Trim(),
                Value = filter.Value.Trim()
            })
            .ToList();

        normalized.EntityTerms = request.EntityTerms
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Select(term => term.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Intent == AiChatIntent.RecommendPicks &&
            normalized.RequestedMarkets.Count > 1)
        {
            normalized.Intent = AiChatIntent.MixedMarketRecommendation;
        }

        if (normalized.Intent == AiChatIntent.MixedMarketRecommendation)
        {
            normalized.FlexibleMix = true;
        }

        if (normalized.Intent == AiChatIntent.ValueBetRequest)
        {
            normalized.ValueBias = true;
        }

        if (normalized.Intent is AiChatIntent.RecommendPicks or AiChatIntent.MixedMarketRecommendation &&
            string.Equals(normalized.Scope, "today", StringComparison.OrdinalIgnoreCase) &&
            !request.RequestedFilters.Any(filter => filter.Name.Equals("bookableOnly", StringComparison.OrdinalIgnoreCase)))
        {
            normalized.BookableOnly = true;
        }

        return normalized;
    }

    private static List<AiChatRequestedMarket> ExtractRequestedMarkets(string userPrompt)
    {
        if (string.IsNullOrWhiteSpace(userPrompt))
        {
            return [];
        }

        var requestedMarkets = new List<AiChatRequestedMarket>();
        foreach (Match match in ExplicitMarketCountRegex().Matches(userPrompt))
        {
            var rawMarket = match.Groups["market"].Success
                ? match.Groups["market"].Value
                : match.Groups["marketAfter"].Value;
            var category = NormalizeMarketCategory(rawMarket);
            if (category is null)
            {
                continue;
            }

            var rawCount = match.Groups["count"].Success
                ? match.Groups["count"].Value
                : match.Groups["countAfter"].Value;
            var resolvedCount = ResolveCount(rawCount);
            if (!resolvedCount.HasValue || resolvedCount <= 0)
            {
                continue;
            }

            var existing = requestedMarkets.FirstOrDefault(market =>
                string.Equals(market.PredictionCategory, category, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                requestedMarkets.Add(new AiChatRequestedMarket
                {
                    PredictionCategory = category,
                    Count = Math.Min(resolvedCount.Value, MaxRequestedCandidates),
                    ExplicitCount = true
                });
                continue;
            }

            existing.Count = Math.Min((existing.Count ?? 0) + resolvedCount.Value, MaxRequestedCandidates);
            existing.ExplicitCount = true;
        }

        return requestedMarkets;
    }

    private static int? ExtractRequestedTotalCount(string userPrompt, HashSet<string> tokens)
    {
        if (string.IsNullOrWhiteSpace(userPrompt))
        {
            return null;
        }

        var totalMatch = TotalCountRegex().Match(userPrompt);
        if (totalMatch.Success)
        {
            return ClampCount(ResolveCount(totalMatch.Groups["count"].Value));
        }

        var pickMatch = GenericPickCountRegex().Match(userPrompt);
        if (pickMatch.Success)
        {
            return ClampCount(ResolveCount(pickMatch.Groups["count"].Value));
        }

        if (tokens.SetEquals(["give", "strong", "picks"]) || tokens.SetEquals(["strong", "picks"]))
        {
            return 5;
        }

        return null;
    }

    private static AiChatIntent DetectIntent(
        string prompt,
        string promptLower,
        HashSet<string> tokens,
        IReadOnlyList<AiChatRequestedMarket> requestedMarkets,
        bool hasWorkingSlip,
        bool hasContextCandidates)
    {
        var mentionedMarketCount = requestedMarkets
            .Select(market => market.PredictionCategory)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        if (mentionedMarketCount == 0)
        {
            mentionedMarketCount = DetectMarketMentions(promptLower).Count;
        }

        if (AiChatContextBuilder.ContainsSecuritySensitiveTopic(promptLower))
        {
            return AiChatIntent.SecurityRefusal;
        }

        if (AiChatContextBuilder.IsWorkingSlipRefinementPrompt(promptLower, hasWorkingSlip))
        {
            return AiChatIntent.WorkingSlipRefinement;
        }

        if (AiChatContextBuilder.IsSettlementPrompt(promptLower, hasContextCandidates))
        {
            return AiChatIntent.SettlementExplanation;
        }

        if (AiChatContextBuilder.IsAppHelpPrompt(promptLower, tokens))
        {
            return AiChatIntent.AppHelp;
        }

        if (IsValueBetRequestPrompt(promptLower))
        {
            return AiChatIntent.ValueBetRequest;
        }

        if (AiChatContextBuilder.IsMatchDiscussionPrompt(promptLower, hasWorkingSlip, hasContextCandidates))
        {
            return AiChatIntent.MatchDiscussion;
        }

        if (mentionedMarketCount > 1 ||
            ((promptLower.Contains("mixture", StringComparison.Ordinal) ||
              promptLower.Contains("across", StringComparison.Ordinal)) &&
             mentionedMarketCount != 1))
        {
            return AiChatIntent.MixedMarketRecommendation;
        }

        return AiChatIntent.RecommendPicks;
    }

    private static bool IsValueBetRequestPrompt(string promptLower)
    {
        if (string.IsNullOrWhiteSpace(promptLower))
        {
            return false;
        }

        if (promptLower.Contains("what is expected value", StringComparison.Ordinal) ||
            promptLower.Contains("what is ev", StringComparison.Ordinal) ||
            promptLower.Contains("explain ev", StringComparison.Ordinal) ||
            promptLower.Contains("what is clv", StringComparison.Ordinal) ||
            promptLower.Contains("what does clv mean", StringComparison.Ordinal))
        {
            return false;
        }

        return promptLower.Contains("highest ev", StringComparison.Ordinal) ||
               promptLower.Contains("best ev", StringComparison.Ordinal) ||
               promptLower.Contains("top ev", StringComparison.Ordinal) ||
               promptLower.Contains("highest expected value", StringComparison.Ordinal) ||
               promptLower.Contains("best value bets", StringComparison.Ordinal) ||
               promptLower.Contains("strong value picks", StringComparison.Ordinal) ||
               promptLower.Contains("most mispriced", StringComparison.Ordinal) ||
               promptLower.Contains("mispriced picks", StringComparison.Ordinal) ||
               promptLower.Contains("value-positive", StringComparison.Ordinal);
    }

    private static string DetermineScope(string promptLower, HashSet<string> tokens, AiChatIntent intent, bool isRolloverIntent)
    {
        if (promptLower.Contains("yesterday", StringComparison.Ordinal))
        {
            return "yesterday";
        }

        if (intent == AiChatIntent.SettlementExplanation)
        {
            return "recent_finished";
        }

        if (intent is AiChatIntent.RecommendPicks or AiChatIntent.MixedMarketRecommendation or AiChatIntent.ValueBetRequest || isRolloverIntent)
        {
            return "today";
        }

        if (tokens.Contains("today"))
        {
            return "today";
        }

        return "recent_window";
    }

    private static bool DetermineBookableOnly(AiChatIntent intent, string scope)
    {
        return intent is AiChatIntent.RecommendPicks or AiChatIntent.MixedMarketRecommendation or AiChatIntent.ValueBetRequest &&
               string.Equals(scope, "today", StringComparison.OrdinalIgnoreCase);
    }

    private static string DetectActionDirective(string promptLower, bool wantsBooking, bool isRolloverIntent, double? targetCombinedOdds)
    {
        if (promptLower.Contains("swap", StringComparison.Ordinal) && promptLower.Contains("draw", StringComparison.Ordinal))
        {
            return "swap_draw_out";
        }

        if (promptLower.Contains("remove", StringComparison.Ordinal) || promptLower.Contains("weakest", StringComparison.Ordinal))
        {
            return "remove_weakest";
        }

        if (promptLower.Contains("riskiest", StringComparison.Ordinal))
        {
            return "show_riskiest";
        }

        if (promptLower.Contains("safer", StringComparison.Ordinal))
        {
            return "make_safer";
        }

        if (targetCombinedOdds.HasValue || isRolloverIntent || promptLower.Contains("odds", StringComparison.Ordinal))
        {
            return "target_odds";
        }

        if (wantsBooking)
        {
            return "book";
        }

        return string.Empty;
    }

    private static List<AiChatRequestedFilter> BuildRequestedFilters(
        string scope,
        bool bookableOnly,
        string safetyBias,
        bool valueBias,
        bool wantsBooking)
    {
        var filters = new List<AiChatRequestedFilter>
        {
            new() { Name = "scope", Value = scope }
        };

        if (bookableOnly)
        {
            filters.Add(new AiChatRequestedFilter { Name = "bookableOnly", Value = "true" });
        }

        if (!string.IsNullOrWhiteSpace(safetyBias))
        {
            filters.Add(new AiChatRequestedFilter { Name = "safetyBias", Value = safetyBias });
        }

        if (valueBias)
        {
            filters.Add(new AiChatRequestedFilter { Name = "valueBias", Value = "true" });
        }

        if (wantsBooking)
        {
            filters.Add(new AiChatRequestedFilter { Name = "wantsBooking", Value = "true" });
        }

        return filters;
    }

    private static List<string> BuildInterpretationNotes(
        string promptLower,
        IReadOnlyList<AiChatRequestedMarket> requestedMarkets,
        int? requestedTotalCount,
        double? targetCombinedOdds,
        string scope)
    {
        var notes = new List<string>();

        foreach (var quantityWord in QuantityWordCounts.Keys)
        {
            if (promptLower.Contains(quantityWord, StringComparison.Ordinal))
            {
                notes.Add($"Interpreted '{quantityWord}' as {QuantityWordCounts[quantityWord]}.");
            }
        }

        if (requestedMarkets.Count > 1 && requestedTotalCount.HasValue && !requestedMarkets.Any(market => market.ExplicitCount))
        {
            notes.Add($"Interpreted this as a mixed-market request for {requestedTotalCount.Value} total picks across the named markets.");
        }

        if (targetCombinedOdds.HasValue)
        {
            notes.Add($"Interpreted the target combined odds as {targetCombinedOdds.Value:0.##}.");
        }

        if (requestedMarkets.Count > 0)
        {
            var scopeLabel = string.Equals(scope, "today", StringComparison.OrdinalIgnoreCase)
                ? "today's published card"
                : scope.Replace('_', ' ');

            if (requestedMarkets.Any(market => market.Count.HasValue))
            {
                var summary = string.Join(
                    ", ",
                    requestedMarkets.Select(market => $"{market.Count ?? 0} {market.DisplayName}"));
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    notes.Add($"Interpreted request as: {summary} picks from {scopeLabel}.");
                }
            }
            else if (requestedTotalCount.HasValue)
            {
                var summary = string.Join(", ", requestedMarkets.Select(market => market.DisplayName));
                notes.Add($"Interpreted request as a {requestedTotalCount.Value}-pick mix across {summary} from {scopeLabel}.");
            }
        }

        return notes;
    }

    private static List<string> ExtractEntityTerms(
        string prompt,
        IReadOnlyList<AiChatRequestedMarket> requestedMarkets,
        AiChatIntent intent)
    {
        if (intent is AiChatIntent.SecurityRefusal or AiChatIntent.AppHelp or AiChatIntent.SettlementExplanation or AiChatIntent.ValueBetRequest)
        {
            return [];
        }

        var tokens = AiChatContextBuilder.ExtractSpecificTokens(prompt)
            .Where(token => !QuantityWords.Contains(token))
            .Where(token => !requestedMarkets.Any(market =>
                string.Equals(market.PredictionCategory, NormalizeMarketCategory(token), StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return tokens;
    }

    private static bool DetectFlexibleMix(string promptLower, IReadOnlyList<AiChatRequestedMarket> requestedMarkets)
    {
        return requestedMarkets.Count > 1 &&
               (promptLower.Contains("mix", StringComparison.Ordinal) ||
                promptLower.Contains("mixture", StringComparison.Ordinal) ||
                promptLower.Contains("across", StringComparison.Ordinal) ||
                promptLower.Contains("combination", StringComparison.Ordinal) ||
                promptLower.Contains("combo", StringComparison.Ordinal));
    }

    private static bool DetermineNeedsSemanticFallback(
        string promptLower,
        AiChatIntent intent,
        IReadOnlyList<AiChatRequestedMarket> requestedMarkets,
        int? requestedTotalCount,
        IReadOnlyList<string> entityTerms,
        double? targetCombinedOdds,
        bool hasWorkingSlip,
        bool hasContextCandidates)
    {
        if (intent is AiChatIntent.SecurityRefusal or AiChatIntent.AppHelp or AiChatIntent.SettlementExplanation)
        {
            return false;
        }

        if (intent == AiChatIntent.WorkingSlipRefinement && hasWorkingSlip)
        {
            return false;
        }

        if (intent == AiChatIntent.MatchDiscussion && (entityTerms.Count > 0 || hasContextCandidates))
        {
            return false;
        }

        var ambiguousCapabilityWording =
            promptLower.Contains("across", StringComparison.Ordinal) ||
            promptLower.Contains("banker", StringComparison.Ordinal) ||
            promptLower.Contains("coupon", StringComparison.Ordinal) ||
            promptLower.Contains("sort me", StringComparison.Ordinal) ||
            promptLower.Contains("line me up", StringComparison.Ordinal);

        if (ambiguousCapabilityWording &&
            intent is AiChatIntent.RecommendPicks or AiChatIntent.MixedMarketRecommendation or AiChatIntent.ValueBetRequest)
        {
            return true;
        }

        if (requestedMarkets.Count > 0 || requestedTotalCount.HasValue || targetCombinedOdds.HasValue)
        {
            return false;
        }

        return ambiguousCapabilityWording ||
               QuantityWords.Any(quantityWord => promptLower.Contains(quantityWord, StringComparison.Ordinal));
    }

    private static List<string> DetectMarketMentions(string promptLower)
    {
        var markets = new List<string>();
        foreach (Match match in MarketMentionRegex().Matches(promptLower))
        {
            var category = NormalizeMarketCategory(match.Value);
            if (category is not null && !markets.Contains(category, StringComparer.OrdinalIgnoreCase))
            {
                markets.Add(category);
            }
        }

        return markets;
    }

    private static string ResolveReferencedContextMode(string promptLower, AiChatSessionState? sessionState)
    {
        if (string.IsNullOrWhiteSpace(promptLower))
        {
            return sessionState?.LastIntent ?? string.Empty;
        }

        var mentionsContext = promptLower.Contains("this", StringComparison.Ordinal) ||
                              promptLower.Contains("that", StringComparison.Ordinal) ||
                              promptLower.Contains("these", StringComparison.Ordinal) ||
                              promptLower.Contains("them", StringComparison.Ordinal) ||
                              promptLower.Contains("those", StringComparison.Ordinal) ||
                              promptLower.Contains("last", StringComparison.Ordinal) ||
                              promptLower.Contains("previous", StringComparison.Ordinal);

        return mentionsContext ? sessionState?.LastIntent ?? string.Empty : string.Empty;
    }

    private static string DetectSafetyBias(string promptLower)
    {
        if (promptLower.Contains("safe", StringComparison.Ordinal) ||
            promptLower.Contains("safer", StringComparison.Ordinal) ||
            promptLower.Contains("banker", StringComparison.Ordinal) ||
            promptLower.Contains("bankers", StringComparison.Ordinal))
        {
            return "safer";
        }

        if (promptLower.Contains("strong", StringComparison.Ordinal) ||
            promptLower.Contains("stronger", StringComparison.Ordinal) ||
            promptLower.Contains("best", StringComparison.Ordinal) ||
            promptLower.Contains("top", StringComparison.Ordinal))
        {
            return "strong";
        }

        return string.Empty;
    }

    private static bool DetectValueBias(string promptLower, AiChatIntent intent)
    {
        return intent == AiChatIntent.ValueBetRequest ||
               promptLower.Contains("value", StringComparison.Ordinal) ||
               promptLower.Contains("mispriced", StringComparison.Ordinal) ||
               promptLower.Contains("ev", StringComparison.Ordinal);
    }

    private static bool MentionsBookingIntent(string promptLower)
    {
        return promptLower.Contains("book", StringComparison.Ordinal) ||
               promptLower.Contains("add all", StringComparison.Ordinal) ||
               promptLower.Contains("open slip", StringComparison.Ordinal) ||
               promptLower.Contains("add to slip", StringComparison.Ordinal);
    }

    private static string NormalizeScope(string scope)
    {
        var normalized = (scope ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "today" => "today",
            "yesterday" => "yesterday",
            "recent_finished" => "recent_finished",
            "recent" => "recent_window",
            "recent_window" => "recent_window",
            _ => "today"
        };
    }

    private static string NormalizeSafetyBias(string safetyBias)
    {
        var normalized = (safetyBias ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "safe" => "safer",
            "safer" => "safer",
            "strong" => "strong",
            "stronger" => "strong",
            "best" => "strong",
            "top" => "strong",
            _ => string.Empty
        };
    }

    private static string NormalizeActionDirective(string actionDirective)
    {
        var normalized = (actionDirective ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "swap_draw_out" => normalized,
            "remove_weakest" => normalized,
            "show_riskiest" => normalized,
            "make_safer" => normalized,
            "target_odds" => normalized,
            "book" => normalized,
            _ => string.Empty
        };
    }

    private static string? NormalizeMarketCategory(string rawMarket)
    {
        var normalized = (rawMarket ?? string.Empty).Trim().ToLowerInvariant().Replace(" ", string.Empty);

        if (normalized.Contains("btts") ||
            normalized.Contains("bothteamstoscore") ||
            normalized.Contains("bothteamsscore") ||
            normalized.Contains("goalgoal") ||
            normalized == "bts" ||
            normalized == "gg")
        {
            return "BothTeamsScore";
        }

        if (normalized.Contains("over2.5") || normalized == "overs" || normalized == "over")
        {
            return "Over2.5Goals";
        }

        if (normalized.Contains("under2.5") || normalized == "unders" || normalized == "under")
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
            normalized == "wins" ||
            normalized == "straights")
        {
            return "StraightWin";
        }

        return null;
    }

    private static int? ResolveCount(string rawCount)
    {
        if (string.IsNullOrWhiteSpace(rawCount))
        {
            return null;
        }

        var normalized = rawCount.Trim().ToLowerInvariant();
        if (QuantityWordCounts.TryGetValue(normalized, out var quantityWordCount))
        {
            return quantityWordCount;
        }

        return int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out var numericCount)
            ? numericCount
            : null;
    }

    private static int? ClampCount(int? count)
    {
        if (!count.HasValue || count.Value <= 0)
        {
            return null;
        }

        return Math.Min(count.Value, MaxRequestedCandidates);
    }

    private static double? ClampTargetOdds(double? targetCombinedOdds)
    {
        if (!targetCombinedOdds.HasValue || targetCombinedOdds < 1.05 || targetCombinedOdds > 100d)
        {
            return null;
        }

        return Math.Round(targetCombinedOdds.Value, 2);
    }

    [GeneratedRegex(
        @"(?:(?:\b(?:a\s+)?(?<count>\d{1,3}|couple|few|several|handful)\b)\s*(?:of\s+)?(?<market>\b(?:both teams to score|both teams score|goal\s*goal|goalgoal|btts|bts|gg|over\s*2(?:\.|,)?5|over2(?:\.|,)?5|under\s*2(?:\.|,)?5|under2(?:\.|,)?5|straight wins?|straightwins?|straightwin|straights|1x2|home wins?|away wins?|wins?|draws?|draw|overs?(?!\s*2(?:\.|,)?5)|unders?(?!\s*2(?:\.|,)?5))\b))|(?:(?<marketAfter>\b(?:both teams to score|both teams score|goal\s*goal|goalgoal|btts|bts|gg|over\s*2(?:\.|,)?5|over2(?:\.|,)?5|under\s*2(?:\.|,)?5|under2(?:\.|,)?5|straight wins?|straightwins?|straightwin|straights|1x2|home wins?|away wins?|wins?|draws?|draw|overs?(?!\s*2(?:\.|,)?5)|unders?(?!\s*2(?:\.|,)?5))\b)\s*(?<countAfter>\d{1,3}|couple|few|several|handful)\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ExplicitMarketCountRegex();

    [GeneratedRegex(@"\b(?:total(?:\s+of)?\s*(?<count>\d{1,3}|couple|few|several|handful)|(?<count>\d{1,3}|couple|few|several|handful)\s*total)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TotalCountRegex();

    [GeneratedRegex(@"\b(?<count>\d{1,3}|couple|few|several|handful)\s*(?:strong|safe|safer|best|top)?\s*(?:pick|picks|prediction|predictions|tip|tips|game|games|match|matches|leg|legs)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex GenericPickCountRegex();

    [GeneratedRegex(@"\b(?:both teams to score|both teams score|goal\s*goal|goalgoal|btts|bts|gg|over\s*2(?:\.|,)?5|over2(?:\.|,)?5|under\s*2(?:\.|,)?5|under2(?:\.|,)?5|draws?|draw|straight wins?|straightwins?|straightwin|straights|1x2|wins?|overs?(?!\s*2(?:\.|,)?5)|unders?(?!\s*2(?:\.|,)?5))\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MarketMentionRegex();
}
