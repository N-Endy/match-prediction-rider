using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Infrastructure.Services;

public class AiAppKnowledgeBase : IAiAppKnowledgeBase
{
    private readonly List<AppKnowledgeEntry> _entries;

    public AiAppKnowledgeBase()
    {
        _entries = BuildKnowledgeEntries();
    }

    public bool TryLookup(string query, out AppKnowledgeEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        var normalized = query.ToLowerInvariant();

        // 1. Direct phrase or keyword matches with scoring
        AppKnowledgeEntry? bestMatch = null;
        int maxScore = 0;

        foreach (var candidate in _entries)
        {
            int score = 0;
            if (normalized.Contains(candidate.Title.ToLowerInvariant(), StringComparison.Ordinal))
            {
                score += 15;
            }

            foreach (var kw in candidate.Keywords)
            {
                if (normalized.Contains(kw.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    score += kw.Length > 4 ? 6 : 3;
                }
            }

            if (score > maxScore && score >= 5)
            {
                maxScore = score;
                bestMatch = candidate;
            }
        }

        if (bestMatch != null)
        {
            entry = bestMatch;
            return true;
        }

        return false;
    }

    public IReadOnlyList<AppKnowledgeEntry> GetAllEntries() => _entries.AsReadOnly();

    private static List<AppKnowledgeEntry> BuildKnowledgeEntries()
    {
        return
        [
            new AppKnowledgeEntry
            {
                TopicId = "straight-win",
                Title = "Straight Win (1X2)",
                Category = "BettingMarket",
                Summary = "Straight Win predictions predict the full-time winner (Home or Away). The model evaluates recent form, team strength ratings, and home advantage.",
                Keywords = ["straight win", "home win", "away win", "1x2", "match winner", "who will win", "win market"],
                Cards =
                [
                    new AiChatKnowledgeCard
                    {
                        Title = "Straight Win (1X2)",
                        Body = "Predicts which team wins the match at full time. Picks must clear a calibrated probability gate before appearing on the page.",
                        Kind = "market"
                    },
                    new AiChatKnowledgeCard
                    {
                        Title = "Reading the Confidence",
                        Body = "The percentage shown is the calibrated probability of that team winning, adjusted for historical model honesty.",
                        Kind = "market"
                    }
                ]
            },
            new AppKnowledgeEntry
            {
                TopicId = "over-under-25",
                Title = "Over/Under 2.5 Goals",
                Category = "BettingMarket",
                Summary = "Predicts whether a match will have 3 or more total goals (Over 2.5) or 2 or fewer goals (Under 2.5) by analyzing team goal rates and defensive strength.",
                Keywords = ["over 2.5", "under 2.5", "over/under", "total goals", "goals market", "how many goals", "scoring pace"],
                Cards =
                [
                    new AiChatKnowledgeCard
                    {
                        Title = "Over/Under 2.5",
                        Body = "Over 2.5 requires 3 or more total match goals. Under 2.5 requires 2 or fewer total goals (e.g. 0-0, 1-0, 1-1, 2-0).",
                        Kind = "market"
                    },
                    new AiChatKnowledgeCard
                    {
                        Title = "Model Evaluation",
                        Body = "Calculated using Poisson and Dixon-Coles expected goals (xG) based on home and away attacking/defensive form splits.",
                        Kind = "market"
                    }
                ]
            },
            new AppKnowledgeEntry
            {
                TopicId = "btts",
                Title = "Both Teams to Score (BTTS)",
                Category = "BettingMarket",
                Summary = "BTTS Yes predicts both teams will score at least one goal in regular time. BTTS No predicts at least one team fails to score.",
                Keywords = ["btts", "both teams to score", "goal goal", "gg", "clean sheet", "both score"],
                Cards =
                [
                    new AiChatKnowledgeCard
                    {
                        Title = "Both Teams to Score (BTTS)",
                        Body = "BTTS Yes lands if both sides score (e.g., 1-1, 2-1). It misses if either team is held scoreless (e.g., 1-0, 0-0).",
                        Kind = "market"
                    },
                    new AiChatKnowledgeCard
                    {
                        Title = "Key Model Factors",
                        Body = "The model evaluates clean sheet percentage, failure-to-score rates, and goal distribution tendencies for each club.",
                        Kind = "market"
                    }
                ]
            },
            new AppKnowledgeEntry
            {
                TopicId = "draws",
                Title = "Match Draws",
                Category = "BettingMarket",
                Summary = "Draw predictions identify tight, low-scoring fixtures where both sides have closely matched ratings and high parity.",
                Keywords = ["draw", "draws", "tie", "draw market", "stalemate", "split points"],
                Cards =
                [
                    new AiChatKnowledgeCard
                    {
                        Title = "Draw Predictions",
                        Body = "Draws are high-value, higher-variance outcomes. The app focuses on low-scoring fixtures where Poisson parity is elevated.",
                        Kind = "market"
                    }
                ]
            },
            new AppKnowledgeEntry
            {
                TopicId = "banker-slips",
                Title = "Banker Slips",
                Category = "Strategy",
                Summary = "Banker Slips are curated multi-match accumulators constructed exclusively from highest-confidence, low-variance selections.",
                Keywords = ["banker", "banker slip", "safe bet", "safe slip", "low risk", "sure bet", "high confidence slip"],
                Cards =
                [
                    new AiChatKnowledgeCard
                    {
                        Title = "Banker Slips",
                        Body = "Built for disciplined compounding. Features picks that comfortably clear their threshold with high probability and minimal volatility.",
                        Kind = "strategy"
                    }
                ]
            },
            new AppKnowledgeEntry
            {
                TopicId = "weekend-payout",
                Title = "Weekend Payout",
                Category = "Strategy",
                Summary = "Weekend Payout compiles high-upside accumulator slips across prominent European league fixtures scheduled for Saturday and Sunday.",
                Keywords = ["weekend payout", "weekend slip", "saturday matches", "sunday matches", "weekend accumulator", "weekend picks"],
                Cards =
                [
                    new AiChatKnowledgeCard
                    {
                        Title = "Weekend Payout",
                        Body = "Targets higher combined multiplier odds across top-tier European fixtures scheduled across the upcoming weekend.",
                        Kind = "strategy"
                    }
                ]
            },
            new AppKnowledgeEntry
            {
                TopicId = "value-bets",
                Title = "Value Bets",
                Category = "Analytics",
                Summary = "Value Bets identify discrepancies where the model's calibrated probability exceeds the implied probability from bookmaker odds.",
                Keywords = ["value bet", "value bets", "ev", "edge", "mispriced", "beating the bookmaker", "positive edge"],
                Cards =
                [
                    new AiChatKnowledgeCard
                    {
                        Title = "What is a Value Bet?",
                        Body = "A value bet occurs when the model calculates a higher probability of an outcome occurring than the odds imply.",
                        Kind = "value-bets"
                    },
                    new AiChatKnowledgeCard
                    {
                        Title = "Expected Value (EV)",
                        Body = "Ranked using EV% = (Model Probability x Decimal Odds) - 1. Picks require threshold clearance and positive edge.",
                        Kind = "value-bets"
                    }
                ]
            },
            new AppKnowledgeEntry
            {
                TopicId = "brier-score",
                Title = "Brier Score",
                Category = "Analytics",
                Summary = "Brier Score is a mathematical metric measuring probability calibration accuracy. Lower scores indicate superior probabilistic precision.",
                Keywords = ["brier", "brier score", "calibration score", "accuracy metric", "how accurate", "forecast quality"],
                Cards =
                [
                    new AiChatKnowledgeCard
                    {
                        Title = "Brier Score",
                        Body = "Measures probability honesty. Rewards confident correct calls and penalizes overconfidence on misses. Lower is better.",
                        Kind = "analytics"
                    }
                ]
            },
            new AppKnowledgeEntry
            {
                TopicId = "reliability",
                Title = "Reliability",
                Category = "Analytics",
                Summary = "Reliability measures calibration honesty over time. If the model predicts a 70% probability, does it land 70% of the time?",
                Keywords = ["reliability", "calibration curve", "calibration honesty", "is the model reliable"],
                Cards =
                [
                    new AiChatKnowledgeCard
                    {
                        Title = "Reliability",
                        Body = "Measures whether stated probabilities match real-world outcomes over large samples. Lower reliability error is better.",
                        Kind = "analytics"
                    }
                ]
            },
            new AppKnowledgeEntry
            {
                TopicId = "booking-codes",
                Title = "SportyBet Booking Codes",
                Category = "Booking",
                Summary = "The app automatically converts selected working slip predictions into SportyBet booking codes for instant placement.",
                Keywords = ["booking code", "sportybet", "book all", "book slip", "booking", "bet code", "booking service"],
                Cards =
                [
                    new AiChatKnowledgeCard
                    {
                        Title = "SportyBet Booking",
                        Body = "Click 'Book All' or ask the AI to generate a booking code. The app maps selections directly to SportyBet market IDs.",
                        Kind = "booking"
                    }
                ]
            },
            new AppKnowledgeEntry
            {
                TopicId = "rollover-strategy",
                Title = "Rollover Strategy",
                Category = "Strategy",
                Summary = "A disciplined betting methodology where profits from one low-odds wager are rolled over into the next target odds stage.",
                Keywords = ["rollover", "rollover strategy", "compounding", "target odds", "odds ladder", "2 odds rollover"],
                Cards =
                [
                    new AiChatKnowledgeCard
                    {
                        Title = "Rollover Compounding",
                        Body = "Selects tightly bounded 1.5 - 2.0 total odds slips designed for step-by-step capital growth across consecutive days.",
                        Kind = "strategy"
                    }
                ]
            },
            new AppKnowledgeEntry
            {
                TopicId = "settlement-colors",
                Title = "Settlement Colors (Green / Red)",
                Category = "Navigation",
                Summary = "Explains why fixtures turn green (hit), red (miss), or stay pending in the MatchPredictor interface.",
                Keywords = ["why is this red", "why is this green", "settlement", "red color", "green color", "settled red", "settled green"],
                Cards =
                [
                    new AiChatKnowledgeCard
                    {
                        Title = "Settlement Colors",
                        Body = "Green indicates the match finished and the actual score matched the prediction. Red indicates the prediction missed. Pending means score sync is awaiting official confirmation.",
                        Kind = "settlement"
                    }
                ]
            }
        ];
    }
}
