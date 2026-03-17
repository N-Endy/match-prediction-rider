using System.Globalization;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Infrastructure.Services;

public class AiChatKnowledgeService
{
    public bool TryBuildSecurityRefusal(string userPrompt, out AiChatResponse response)
    {
        if (!ContainsSensitiveTopic(userPrompt))
        {
            response = new AiChatResponse();
            return false;
        }

        response = new AiChatResponse
        {
            Message = "I can help with public predictions, settled matches, value bets, and analytics, but I can't reveal passwords, API keys, connection strings, admin credentials, or private operator details.",
            KnowledgeCards =
            [
                new AiChatKnowledgeCard
                {
                    Title = "Public Scope Only",
                    Body = "Ask me about today's picks, recent settled matches, score colors, value-bet logic, analytics terms, or how the visible parts of MatchPredictor work.",
                    Kind = "security"
                }
            ]
        };

        return true;
    }

    public bool TryBuildPublicAppHelpResponse(
        string userPrompt,
        IReadOnlyList<AiChatContextBuilder.AiChatContextCandidate> contextCandidates,
        out AiChatResponse response,
        out string knowledgeTopic)
    {
        var prompt = userPrompt.ToLowerInvariant();

        if (prompt.Contains("why is this red", StringComparison.Ordinal) ||
            prompt.Contains("why did this settle red", StringComparison.Ordinal) ||
            prompt.Contains("why is this green", StringComparison.Ordinal) ||
            prompt.Contains("why did this settle green", StringComparison.Ordinal))
        {
            knowledgeTopic = "settlement";
            response = BuildSettlementColorResponse(contextCandidates);
            return true;
        }

        if (prompt.Contains("reliability", StringComparison.Ordinal))
        {
            knowledgeTopic = "analytics";
            response = new AiChatResponse
            {
                Message = "Reliability measures how closely forecast probabilities matched what actually happened. Lower is better because it means a 70% call behaved more like a real 70% call over time.",
                KnowledgeCards =
                [
                    new AiChatKnowledgeCard
                    {
                        Title = "Reliability",
                        Body = "This is calibration honesty. If the model says 70%, reliability checks whether similar 70% forecasts really landed near 70% over time.",
                        Kind = "analytics"
                    },
                    new AiChatKnowledgeCard
                    {
                        Title = "How To Read It",
                        Body = "Lower reliability is better. On the analytics page, curves closer to the diagonal also mean the probabilities are better calibrated.",
                        Kind = "analytics"
                    }
                ]
            };
            return true;
        }

        if (prompt.Contains("brier", StringComparison.Ordinal))
        {
            knowledgeTopic = "analytics";
            response = new AiChatResponse
            {
                Message = "Brier score is the main probability-quality score on the analytics page. Lower is better because it rewards both being correct and being honest about uncertainty.",
                KnowledgeCards =
                [
                    new AiChatKnowledgeCard
                    {
                        Title = "Brier Score",
                        Body = "A forecast gets rewarded for being right, but it also gets punished for being overconfident when it misses.",
                        Kind = "analytics"
                    }
                ]
            };
            return true;
        }

        if (prompt.Contains("resolution", StringComparison.Ordinal))
        {
            knowledgeTopic = "analytics";
            response = new AiChatResponse
            {
                Message = "Resolution shows whether the model meaningfully separates stronger spots from marginal ones. Higher is better because it means the probabilities are not all collapsing into the same middle range.",
                KnowledgeCards =
                [
                    new AiChatKnowledgeCard
                    {
                        Title = "Resolution",
                        Body = "High resolution means the model is actually distinguishing stronger edges from weaker ones instead of giving everything nearly the same probability.",
                        Kind = "analytics"
                    }
                ]
            };
            return true;
        }

        if (prompt.Contains("uncertainty", StringComparison.Ordinal))
        {
            knowledgeTopic = "analytics";
            response = new AiChatResponse
            {
                Message = "Uncertainty is the background difficulty of the market itself. It is descriptive, not a quality score, so it tells you how noisy the market is rather than how good the model was.",
                KnowledgeCards =
                [
                    new AiChatKnowledgeCard
                    {
                        Title = "Uncertainty",
                        Body = "This is the market's built-in randomness. A market can be hard even when the model is behaving well.",
                        Kind = "analytics"
                    }
                ]
            };
            return true;
        }

        if (prompt.Contains("threshold source", StringComparison.Ordinal) ||
            prompt.Contains("threshold", StringComparison.Ordinal))
        {
            knowledgeTopic = "thresholds";
            response = new AiChatResponse
            {
                Message = "Thresholds are the publication gates. A pick normally needs its calibrated probability to clear the live threshold before it gets published. 'Configured' means the default setting is live, while 'Tuned' means a promoted data-backed threshold is active.",
                KnowledgeCards =
                [
                    new AiChatKnowledgeCard
                    {
                        Title = "Threshold Source",
                        Body = "Configured means the default gate is live. Tuned means the app promoted a threshold based on historical settled data.",
                        Kind = "threshold"
                    }
                ]
            };
            return true;
        }

        if (prompt.Contains("calibrator", StringComparison.Ordinal) ||
            prompt.Contains("calibration", StringComparison.Ordinal))
        {
            knowledgeTopic = "calibration";
            response = new AiChatResponse
            {
                Message = "Calibration is the step that reshapes raw model probabilities so they behave more honestly over time. Calibrator eras on the analytics page show how forecasts performed while a specific calibrator was live.",
                KnowledgeCards =
                [
                    new AiChatKnowledgeCard
                    {
                        Title = "Calibrator Era",
                        Body = "This helps you see whether probability quality improved or worsened while a given calibrator was active.",
                        Kind = "analytics"
                    }
                ]
            };
            return true;
        }

        if ((prompt.Contains("value bets", StringComparison.Ordinal) || prompt.Contains("value bet", StringComparison.Ordinal)) &&
            (prompt.Contains("why isn't", StringComparison.Ordinal) || prompt.Contains("why isnt", StringComparison.Ordinal)))
        {
            knowledgeTopic = "value-bets";
            response = BuildValueBetsExclusionResponse(contextCandidates);
            return true;
        }

        if (prompt.Contains("expected value", StringComparison.Ordinal) ||
            prompt.Contains("ev formula", StringComparison.Ordinal) ||
            prompt.Contains("what is ev", StringComparison.Ordinal) ||
            prompt.Contains("explain ev", StringComparison.Ordinal) ||
            prompt.Contains("value %", StringComparison.Ordinal))
        {
            knowledgeTopic = "expected-value";
            response = new AiChatResponse
            {
                Message = "Expected value in the app is the pricing check: EV% = (model probability x decimal odds) - 1. Positive EV means the model thinks the true chance is better than the price being offered.",
                KnowledgeCards =
                [
                    new AiChatKnowledgeCard
                    {
                        Title = "EV Formula",
                        Body = "The app uses EV% = (model probability x decimal odds) - 1. A 0.12 result means about +12% expected value.",
                        Kind = "value-bets"
                    },
                    new AiChatKnowledgeCard
                    {
                        Title = "How The App Uses It",
                        Body = "Value Bets still require threshold clearance and a positive edge versus market first. EV then becomes the main ranking signal on the page.",
                        Kind = "value-bets"
                    }
                ]
            };
            return true;
        }

        if (prompt.Contains("clv", StringComparison.Ordinal) ||
            prompt.Contains("closing line value", StringComparison.Ordinal) ||
            prompt.Contains("closing line", StringComparison.Ordinal))
        {
            knowledgeTopic = "clv";
            response = new AiChatResponse
            {
                Message = "CLV compares the odds captured when the app published a pick against the closing odds near kickoff. Positive CLV means the earlier captured price was better than the close.",
                KnowledgeCards =
                [
                    new AiChatKnowledgeCard
                    {
                        Title = "CLV Formula",
                        Body = "The tracking layer uses CLV% = (publish odds / closing odds) - 1. Positive CLV means the market moved against that price before kickoff.",
                        Kind = "value-bets"
                    },
                    new AiChatKnowledgeCard
                    {
                        Title = "What It Tells You",
                        Body = "CLV is a market-timing signal, not a guarantee. Beating the close consistently is healthier than judging one bet in isolation.",
                        Kind = "value-bets"
                    }
                ]
            };
            return true;
        }

        if (prompt.Contains("value bets", StringComparison.Ordinal) ||
            prompt.Contains("value bet", StringComparison.Ordinal) ||
            prompt.Contains("mispriced", StringComparison.Ordinal) ||
            (prompt.Contains("edge", StringComparison.Ordinal) && prompt.Contains("market", StringComparison.Ordinal)))
        {
            knowledgeTopic = "value-bets";
            response = new AiChatResponse
            {
                Message = "Value bets are deterministic. A pick needs to clear its calibrated threshold, beat the source market probability by a positive edge, and then it is ranked by expected value using the current decimal price.",
                KnowledgeCards =
                [
                    new AiChatKnowledgeCard
                    {
                        Title = "Value Bet Rules",
                        Body = "The page checks threshold first, then compares model probability against the synced market probability. A positive edge is required before EV ranking matters.",
                        Kind = "value-bets"
                    },
                    new AiChatKnowledgeCard
                    {
                        Title = "EV Ranking",
                        Body = "After the gate passes, the app ranks picks by expected value, using the available decimal odds or a derived fallback when raw odds are missing.",
                        Kind = "value-bets"
                    },
                    new AiChatKnowledgeCard
                    {
                        Title = "Why A Pick Can Be Missing",
                        Body = "Typical reasons are no source price, below-threshold probability, or a non-positive edge versus market.",
                        Kind = "value-bets"
                    }
                ]
            };
            return true;
        }

        if (prompt.Contains("straight wins selected", StringComparison.Ordinal) ||
            prompt.Contains("straight wins", StringComparison.Ordinal) && prompt.Contains("selected", StringComparison.Ordinal) ||
            prompt.Contains("how are straight wins selected", StringComparison.Ordinal))
        {
            knowledgeTopic = "selection";
            response = new AiChatResponse
            {
                Message = "Straight wins come from the model's calibrated 1X2 view. To get published, the pick needs to clear the live threshold, and stronger options tend to have higher confidence, more room above threshold, and cleaner market support when pricing is available.",
                KnowledgeCards =
                [
                    new AiChatKnowledgeCard
                    {
                        Title = "Straight Win Selection",
                        Body = "The app publishes a straight-win pick only after calibration and threshold gating. Safer requests lean toward higher confidence and lower-volatility price profiles.",
                        Kind = "selection"
                    }
                ]
            };
            return true;
        }

        if (prompt.Contains("live", StringComparison.Ordinal) ||
            prompt.Contains("finished", StringComparison.Ordinal) ||
            prompt.Contains("upcoming", StringComparison.Ordinal) ||
            prompt.Contains("red", StringComparison.Ordinal) ||
            prompt.Contains("green", StringComparison.Ordinal))
        {
            knowledgeTopic = "statuses";
            response = new AiChatResponse
            {
                Message = "Upcoming means the fixture has not started yet, Live means the match is in progress or still being refreshed, and Finished means a final score is attached. Green/red settlement comes from comparing the predicted outcome against the actual outcome once the score is settled.",
                KnowledgeCards =
                [
                    new AiChatKnowledgeCard
                    {
                        Title = "Score Colors",
                        Body = "Green means the settled result matched the published pick. Red means the final outcome went the other way.",
                        Kind = "settlement"
                    }
                ]
            };
            return true;
        }

        response = new AiChatResponse();
        knowledgeTopic = string.Empty;
        return false;
    }

    private static AiChatResponse BuildSettlementColorResponse(IReadOnlyList<AiChatContextBuilder.AiChatContextCandidate> contextCandidates)
    {
        var candidate = contextCandidates.FirstOrDefault();
        if (candidate is null)
        {
            return new AiChatResponse
            {
                Message = "I need a match in context to explain a red or green settlement cleanly. Ask me about a specific fixture or a set of picks I just showed you.",
                KnowledgeCards =
                [
                    new AiChatKnowledgeCard
                    {
                        Title = "Settlement Colors",
                        Body = "Green means the settled result matched the published pick. Red means the actual outcome disagreed with the pick after the final score landed.",
                        Kind = "settlement"
                    }
                ]
            };
        }

        if (string.IsNullOrWhiteSpace(candidate.ActualScore) || string.IsNullOrWhiteSpace(candidate.ActualOutcome))
        {
            return new AiChatResponse
            {
                Message = $"I can see {candidate.HomeTeam} vs {candidate.AwayTeam}, but I do not have a final settled outcome attached yet, so I can't confirm a red or green explanation for it.",
                KnowledgeCards =
                [
                    new AiChatKnowledgeCard
                    {
                        Title = "Pending Settlement",
                        Body = "A match can stay pending when the score sync has not locked a final result yet, or when the source still looks live.",
                        Kind = "settlement"
                    }
                ]
            };
        }

        var landed = candidate.PredictedOutcome.Equals(candidate.ActualOutcome, StringComparison.OrdinalIgnoreCase);
        var color = landed ? "green" : "red";
        var outcomeSummary = candidate.ActualOutcome.Equals("Draw", StringComparison.OrdinalIgnoreCase)
            ? "the game finished as a draw"
            : $"the final outcome was {candidate.ActualOutcome}";

        return new AiChatResponse
        {
            Message = $"{candidate.HomeTeam} vs {candidate.AwayTeam} settled {color} because the pick was {candidate.PredictedOutcome}, the final score was {candidate.ActualScore}, and {outcomeSummary}.",
            KnowledgeCards =
            [
                new AiChatKnowledgeCard
                {
                    Title = "Settlement Check",
                    Body = "The app compares the published outcome against the final actual outcome after the score sync settles the fixture.",
                    Kind = "settlement"
                }
            ]
        };
    }

    private static AiChatResponse BuildValueBetsExclusionResponse(IReadOnlyList<AiChatContextBuilder.AiChatContextCandidate> contextCandidates)
    {
        var candidate = contextCandidates.FirstOrDefault();
        if (candidate is null)
        {
            return new AiChatResponse
            {
                Message = "I can't confirm a specific value-bet exclusion without a match in context. In general, a pick stays out when there is no synced source price, it misses the threshold, or the model edge over market is not positive enough.",
                KnowledgeCards =
                [
                    new AiChatKnowledgeCard
                    {
                        Title = "Value Bet Exclusions",
                        Body = "Most exclusions come from missing market pricing, below-threshold model probability, or edge that is too small or negative.",
                        Kind = "value-bets"
                    }
                ]
            };
        }

        var modelProbability = (double)(candidate.ConfidenceScore ?? decimal.Zero);
        if (candidate.MarketProbability is null)
        {
            return new AiChatResponse
            {
                Message = $"I can't confirm a specific value-bet verdict for {candidate.HomeTeam} vs {candidate.AwayTeam} because the current chat context does not include synced market pricing for that pick. Without the market side, I would just be guessing.",
                KnowledgeCards =
                [
                    new AiChatKnowledgeCard
                    {
                        Title = "Missing Pricing",
                        Body = "A pick cannot become a value bet if the page has no synced source probability to compare against the model.",
                        Kind = "value-bets"
                    }
                ]
            };
        }

        if (modelProbability < candidate.ThresholdUsed)
        {
            return new AiChatResponse
            {
                Message = $"{candidate.HomeTeam} vs {candidate.AwayTeam} looks below the publication gate for this market in the current chat context: the calibrated probability is {(modelProbability * 100d):0.0}% against a {(candidate.ThresholdUsed * 100d):0.0}% threshold.",
                KnowledgeCards =
                [
                    new AiChatKnowledgeCard
                    {
                        Title = "Below Threshold",
                        Body = "Value bets still depend on the underlying pick being strong enough to clear its threshold before the market comparison matters.",
                        Kind = "value-bets"
                    }
                ]
            };
        }

        if (candidate.EdgePoints is <= 0)
        {
            return new AiChatResponse
            {
                Message = $"{candidate.HomeTeam} vs {candidate.AwayTeam} does not show a positive model edge in the current chat context. The model is at {(modelProbability * 100d):0.0}% and the synced market sits at {(candidate.MarketProbability.Value * 100d):0.0}%, so there is no real pricing gap to call value.",
                KnowledgeCards =
                [
                    new AiChatKnowledgeCard
                    {
                        Title = "No Positive Edge",
                        Body = "A pick is not a value bet just because it was published. The model still needs to beat the market probability by a real margin.",
                        Kind = "value-bets"
                    }
                ]
            };
        }

        return new AiChatResponse
        {
            Message = $"{candidate.HomeTeam} vs {candidate.AwayTeam} actually looks value-positive in the current chat context: model {(modelProbability * 100d):0.0}% versus market {(candidate.MarketProbability.Value * 100d):0.0}% for a +{candidate.EdgePoints.GetValueOrDefault():0.0} point edge. If it is missing on the value-bets page, the most likely reason is that the report was built from a different pricing snapshot or the page has not refreshed yet.",
            KnowledgeCards =
            [
                new AiChatKnowledgeCard
                {
                    Title = "Value Bet Snapshot",
                    Body = "The value-bets page is snapshot-based. A pick can look value-positive in one synced pricing snapshot and disappear in another if the market moves.",
                    Kind = "value-bets"
                }
            ]
        };
    }

    private static bool ContainsSensitiveTopic(string userPrompt)
    {
        var prompt = userPrompt.ToLowerInvariant();
        return prompt.Contains("password", StringComparison.Ordinal) ||
               prompt.Contains("api key", StringComparison.Ordinal) ||
               prompt.Contains("apikey", StringComparison.Ordinal) ||
               prompt.Contains("groq", StringComparison.Ordinal) && prompt.Contains("key", StringComparison.Ordinal) ||
               prompt.Contains("connection string", StringComparison.Ordinal) ||
               prompt.Contains("admin credential", StringComparison.Ordinal) ||
               prompt.Contains("admin password", StringComparison.Ordinal) ||
               prompt.Contains("hangfire", StringComparison.Ordinal) ||
               prompt.Contains("/ops/health", StringComparison.Ordinal) ||
               prompt.Contains("usage dashboard", StringComparison.Ordinal) ||
               prompt.Contains("secret", StringComparison.Ordinal) ||
               prompt.Contains("token", StringComparison.Ordinal);
    }
}
