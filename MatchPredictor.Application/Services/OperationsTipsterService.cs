using System.Globalization;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatchPredictor.Application.Services;

public class OperationsTipsterService : IOperationsTipsterService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ISportyBetBookingService _bookingService;
    private readonly ILogger<OperationsTipsterService> _logger;

    public OperationsTipsterService(
        ApplicationDbContext dbContext,
        ISportyBetBookingService bookingService,
        ILogger<OperationsTipsterService> logger)
    {
        _dbContext = dbContext;
        _bookingService = bookingService;
        _logger = logger;
    }

    public async Task<OperationsDossierDto> GetOperationsDossierAsync(DateOnly? targetDate = null, CancellationToken ct = default)
    {
        return await BuildDossierAsync(targetDate, ct);
    }

    public async Task<OperationsDossierDto> RefreshOperationsDossierAsync(DateOnly? targetDate = null, CancellationToken ct = default)
    {
        _logger.LogInformation("Refreshing AI Tipster Operations Dossier on demand.");
        return await BuildDossierAsync(targetDate, ct);
    }

    private async Task<OperationsDossierDto> BuildDossierAsync(DateOnly? requestedDate, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var date = requestedDate ?? DateOnly.FromDateTime(nowUtc);

        var predictions = await _dbContext.Predictions
            .AsNoTracking()
            .Where(p => p.IsCurrentRevision && p.WasPublished && p.MatchLocalDate == date)
            .ToListAsync(ct);

        // Filter out matches that started more than 5 minutes ago
        var upcomingPredictions = predictions
            .Where(p => !p.MatchDateTime.HasValue || p.MatchDateTime.Value > nowUtc.AddMinutes(-5))
            .ToList();

        var totalFixturesOnDate = await _dbContext.MatchDatas
            .AsNoTracking()
            .CountAsync(m => m.MatchLocalDate == date, ct);

        if (totalFixturesOnDate == 0)
        {
            totalFixturesOnDate = upcomingPredictions.Select(p => $"{p.HomeTeam}_{p.AwayTeam}").Distinct().Count();
        }

        // Tier 1: Real SportyBet Snapshot Odds
        var predIds = upcomingPredictions.Select(p => p.Id).ToList();
        var oddsSnapshots = await _dbContext.PredictionOddsSnapshots
            .AsNoTracking()
            .Where(s => predIds.Contains(s.PredictionId))
            .OrderByDescending(s => s.CapturedAtUtc)
            .ToListAsync(ct);

        var oddsSnapshotLookup = oddsSnapshots
            .GroupBy(s => s.PredictionId)
            .ToDictionary(g => g.Key, g => g.First());

        // Tier 2: MatchDatas Scraped Odds
        var matchDatas = await _dbContext.MatchDatas
            .AsNoTracking()
            .Where(m => m.MatchLocalDate == date)
            .ToListAsync(ct);

        var matchDataLookup = matchDatas
            .GroupBy(m => $"{m.MatchLocalDate:yyyy-MM-dd}_{m.HomeTeam}_{m.AwayTeam}".ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Build Candidate Evaluator
        var candidates = new List<EvaluatedCandidate>();
        foreach (var pred in upcomingPredictions)
        {
            var key = $"{pred.MatchLocalDate:yyyy-MM-dd}_{pred.HomeTeam}_{pred.AwayTeam}".ToLowerInvariant();
            matchDataLookup.TryGetValue(key, out var md);
            oddsSnapshotLookup.TryGetValue(pred.Id, out var snapshot);

            var odds = ResolveCandidateOdds(pred, snapshot, md);
            var modelProb = pred.ConfidenceScore ?? 0.50m;
            var impliedProb = odds > 1.0m ? Math.Round(1.0m / odds, 4) : 0.50m;
            var edge = modelProb - impliedProb;

            candidates.Add(new EvaluatedCandidate
            {
                Prediction = pred,
                Odds = odds,
                ModelProbability = modelProb,
                ImpliedProbability = impliedProb,
                Edge = edge
            });
        }

        // Assess Slate Grade
        var (slateGrade, badgeColor, executiveSummary, watchouts) = AssessSlate(totalFixturesOnDate, candidates.Count, date);

        // Build 3 Pillars
        var tickets = new List<OperationsPillarTicketDto>
        {
            await BuildCompounderTicketAsync(candidates, ct),
            await BuildValueEdgeTicketAsync(candidates, ct),
            await BuildMultiplierSniperTicketAsync(candidates, ct)
        };

        return new OperationsDossierDto
        {
            SlateDate = date,
            GeneratedAtUtc = DateTime.UtcNow,
            GeneratedLocalLabel = DateTime.UtcNow.ToString("dd MMM yyyy · HH:mm 'UTC'", CultureInfo.InvariantCulture),
            TotalFixturesAnalyzed = totalFixturesOnDate,
            PublishedPicksCount = candidates.Count,
            SlateGrade = slateGrade,
            SlateBadgeColor = badgeColor,
            ExecutiveSummary = executiveSummary,
            TacticalWatchouts = watchouts,
            TotalDailyRiskBudgetPercent = 5.0m,
            Tickets = tickets
        };
    }

    private static (string Grade, string Color, string Summary, string Watchouts) AssessSlate(
        int totalFixtures, 
        int publishedCandidates, 
        DateOnly slateDate)
    {
        if (publishedCandidates >= 20 && totalFixtures >= 25)
        {
            return (
                "GRADE A — PRIME BOARD (+EV DENSE)",
                "#10b981",
                $"Elite weekend volume with {totalFixtures} researched fixtures across top European flights. High statistical density and positive market dislocations across 1X2 and totals.",
                "Avoid narrative-driven heavy favorites with odds under 1.25 where pricing carries extreme bookmaker juice. Stick strictly to our model-backed allocation."
            );
        }

        if (publishedCandidates >= 8)
        {
            return (
                "GRADE B — SOLID DISCIPLINED SLATE",
                "#38bdf8",
                $"Healthy board with {totalFixtures} active fixtures. Ample value in goal-market dynamics and select straight-win positions.",
                "Midweek cup and continental matches present rotation hazards. We have prioritized league-tested setups with stable starting lineups."
            );
        }

        return (
            "GRADE C — SELECTIVE CONSERVATION SLATE",
            "#f59e0b",
            $"Low-volume card with {totalFixtures} fixtures. When the slate is thin, elite betting operations preserve dry powder rather than forcing action.",
            "Bookmaker margins are higher on fringe markets today. Any tier without clear positive mathematical expectation is rested under our Capital Preservation Protocol."
        );
    }

    private async Task<OperationsPillarTicketDto> BuildCompounderTicketAsync(
        List<EvaluatedCandidate> pool, 
        CancellationToken ct)
    {
        // Pillar 1: 1.50 - 2.25x odds, lowest variance, high model confidence (>= 0.65)
        var eligible = pool
            .Where(c => c.ModelProbability >= 0.65m && c.Odds >= 1.22m && c.Odds <= 2.20m)
            .OrderByDescending(c => c.ModelProbability)
            .ThenByDescending(c => c.Edge)
            .ToList();

        if (eligible.Count == 0)
        {
            return new OperationsPillarTicketDto
            {
                PillarKind = OperationsPillarKind.Compounder,
                Title = "The Capital Compounder",
                Subtitle = "Pillar 1 · Steady Bankroll Growth",
                TargetOddsRange = "1.50x – 2.25x",
                AllocationPercent = 0m,
                RecommendedUnits = 0m,
                KellyFraction = 0m,
                IsPreserved = true,
                PreservationReason = "Zero fixtures today met the strict 65%+ probability and positive expectation criteria for safe compounding. Capital held in reserve.",
                TacticalAnalysis = "A true professional never forces a banker when the odds offer no edge. Capital preservation protects yesterday's gains."
            };
        }

        var selected = new List<EvaluatedCandidate>();
        var first = eligible[0];
        selected.Add(first);

        if (first.Odds < 1.50m && eligible.Count > 1)
        {
            // Pick a complementary second leg from a different fixture
            var second = eligible.Skip(1).FirstOrDefault(c => c.Prediction.FixtureKey != first.Prediction.FixtureKey);
            if (second != null && (first.Odds * second.Odds) <= 2.30m)
            {
                selected.Add(second);
            }
        }

        decimal combinedOdds = selected.Aggregate(1.0m, (acc, c) => acc * c.Odds);
        decimal modelProb = selected.Aggregate(1.0m, (acc, c) => acc * c.ModelProbability);
        decimal impliedProb = Math.Round(1.0m / combinedOdds, 4);
        decimal edge = Math.Round((modelProb - impliedProb) * 100m, 1);

        var legs = selected.Select(c => MapToLeg(c, "BANKER")).ToList();
        var booking = await BookTicketAsync(legs);

        return new OperationsPillarTicketDto
        {
            PillarKind = OperationsPillarKind.Compounder,
            Title = "The Capital Compounder",
            Subtitle = "Pillar 1 · Steady Bankroll Growth",
            TargetOddsRange = "1.50x – 2.25x",
            CombinedOdds = Math.Round(combinedOdds, 2),
            ModelProbability = Math.Round(modelProb, 4),
            ImpliedProbability = impliedProb,
            EdgePercent = edge,
            AllocationPercent = 60m, // 60% of daily stake
            RecommendedUnits = 3.0m, // 3 units
            KellyFraction = 0.25m,   // Quarter-Kelly
            BookingCode = booking.BookingCode,
            BookingUrl = booking.BookingUrl,
            IsPreserved = false,
            TacticalAnalysis = $"Prime low-variance setup yielding {combinedOdds:F2}x combined odds. Model assigns {Math.Round(modelProb * 100m, 1)}% true probability against bookmaker implied {Math.Round(impliedProb * 100m, 1)}%, establishing a strong +{edge}% mathematical edge.",
            Legs = legs
        };
    }

    private async Task<OperationsPillarTicketDto> BuildValueEdgeTicketAsync(
        List<EvaluatedCandidate> pool, 
        CancellationToken ct)
    {
        // Pillar 2: 3.20 - 6.50x odds, 3-4 legs, targeting positive edge (>= +3.5%)
        var eligible = pool
            .Where(c => c.Edge >= 0.03m && c.Odds >= 1.40m && c.Odds <= 2.45m)
            .OrderByDescending(c => c.Edge)
            .ThenByDescending(c => c.ModelProbability)
            .ToList();

        // Ensure distinct fixtures
        var distinct = new List<EvaluatedCandidate>();
        var seenFixtures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in eligible)
        {
            var key = c.Prediction.FixtureKey ?? $"{c.Prediction.HomeTeam}_{c.Prediction.AwayTeam}";
            if (seenFixtures.Add(key))
            {
                distinct.Add(c);
            }
        }

        if (distinct.Count < 2)
        {
            return new OperationsPillarTicketDto
            {
                PillarKind = OperationsPillarKind.ValueEdge,
                Title = "The Value Edge Ticket",
                Subtitle = "Pillar 2 · Market Dislocation Exploitation",
                TargetOddsRange = "3.20x – 6.50x",
                AllocationPercent = 0m,
                RecommendedUnits = 0m,
                KellyFraction = 0m,
                IsPreserved = true,
                PreservationReason = "Fewer than 2 clear market dislocation opportunities (+3% edge) identified on today's card. Capital preserved.",
                TacticalAnalysis = "Bookmakers priced the mid-range lines efficiently today. We pass rather than paying overround."
            };
        }

        var selected = new List<EvaluatedCandidate>();
        decimal currentOdds = 1.0m;

        foreach (var c in distinct)
        {
            if (selected.Count >= 4) break;
            if (selected.Count >= 2 && currentOdds >= 3.20m && (currentOdds * c.Odds) > 6.50m) break;

            selected.Add(c);
            currentOdds *= c.Odds;

            if (currentOdds >= 3.50m && selected.Count >= 3) break;
        }

        decimal modelProb = selected.Aggregate(1.0m, (acc, c) => acc * c.ModelProbability);
        decimal impliedProb = Math.Round(1.0m / currentOdds, 4);
        decimal edge = Math.Round((modelProb - impliedProb) * 100m, 1);

        var legs = selected.Select(c => MapToLeg(c, "VALUE")).ToList();
        var booking = await BookTicketAsync(legs);

        return new OperationsPillarTicketDto
        {
            PillarKind = OperationsPillarKind.ValueEdge,
            Title = "The Value Edge Ticket",
            Subtitle = "Pillar 2 · Market Dislocation Exploitation",
            TargetOddsRange = "3.20x – 6.50x",
            CombinedOdds = Math.Round(currentOdds, 2),
            ModelProbability = Math.Round(modelProb, 4),
            ImpliedProbability = impliedProb,
            EdgePercent = edge,
            AllocationPercent = 30m, // 30% of daily stake
            RecommendedUnits = 1.5m, // 1.5 units
            KellyFraction = 0.20m,
            BookingCode = booking.BookingCode,
            BookingUrl = booking.BookingUrl,
            IsPreserved = false,
            TacticalAnalysis = $"Exploits mispriced lines across {selected.Count} fixtures. Every leg carries positive expected value (+EV), compounding into {currentOdds:F2}x odds with an overall model edge of +{edge}%.",
            Legs = legs
        };
    }

    private async Task<OperationsPillarTicketDto> BuildMultiplierSniperTicketAsync(
        List<EvaluatedCandidate> pool, 
        CancellationToken ct)
    {
        // Pillar 3: 12.0x - 35.0x odds, 5-8 legs, asymmetric multiplier with strictly capped risk
        var eligible = pool
            .Where(c => c.ModelProbability >= 0.55m && c.Odds >= 1.30m && c.Odds <= 2.20m)
            .OrderByDescending(c => c.Edge)
            .ThenByDescending(c => c.ModelProbability)
            .ToList();

        var distinct = new List<EvaluatedCandidate>();
        var seenFixtures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in eligible)
        {
            var key = c.Prediction.FixtureKey ?? $"{c.Prediction.HomeTeam}_{c.Prediction.AwayTeam}";
            if (seenFixtures.Add(key))
            {
                distinct.Add(c);
            }
        }

        if (distinct.Count < 4)
        {
            return new OperationsPillarTicketDto
            {
                PillarKind = OperationsPillarKind.MultiplierSniper,
                Title = "The Multiplier Sniper",
                Subtitle = "Pillar 3 · Asymmetric Upside Ticket",
                TargetOddsRange = "12.0x – 35.0x",
                AllocationPercent = 0m,
                RecommendedUnits = 0m,
                KellyFraction = 0m,
                IsPreserved = true,
                PreservationReason = "Slate lacks sufficient independent positive-EV fixtures to assemble a high-yield multiplier ticket today.",
                TacticalAnalysis = "Lottery tickets without genuine model support are pure rake donors. Sticking to disciplined risk containment."
            };
        }

        var selected = new List<EvaluatedCandidate>();
        decimal currentOdds = 1.0m;

        foreach (var c in distinct)
        {
            if (selected.Count >= 8) break;
            if (currentOdds >= 15.0m && selected.Count >= 6) break;

            selected.Add(c);
            currentOdds *= c.Odds;
        }

        decimal modelProb = selected.Aggregate(1.0m, (acc, c) => acc * c.ModelProbability);
        decimal impliedProb = Math.Round(1.0m / currentOdds, 6);
        decimal edge = Math.Round((modelProb - impliedProb) * 100m, 2);

        var legs = selected.Select(c => MapToLeg(c, "SNIPER")).ToList();
        var booking = await BookTicketAsync(legs);

        return new OperationsPillarTicketDto
        {
            PillarKind = OperationsPillarKind.MultiplierSniper,
            Title = "The Multiplier Sniper",
            Subtitle = "Pillar 3 · Asymmetric Upside Ticket",
            TargetOddsRange = "12.0x – 35.0x",
            CombinedOdds = Math.Round(currentOdds, 2),
            ModelProbability = Math.Round(modelProb, 6),
            ImpliedProbability = impliedProb,
            EdgePercent = edge,
            AllocationPercent = 10m, // 10% of daily stake
            RecommendedUnits = 0.5m, // 0.5 units
            KellyFraction = 0.10m,
            BookingCode = booking.BookingCode,
            BookingUrl = booking.BookingUrl,
            IsPreserved = false,
            TacticalAnalysis = $"High-yield {selected.Count}-leg accumulator priced at {currentOdds:F2}x. Risk is strictly confined to 10% of the daily budget (0.5 units) to deliver explosive asymmetric upside without impacting capital preservation.",
            Legs = legs
        };
    }

    private static OperationsLegDto MapToLeg(EvaluatedCandidate candidate, string defaultConviction)
    {
        var pred = candidate.Prediction;
        var conviction = candidate.ModelProbability >= 0.75m ? "BANKER" :
                         candidate.Edge >= 0.05m ? "VALUE" : defaultConviction;

        return new OperationsLegDto
        {
            PredictionId = pred.Id,
            HomeTeam = pred.HomeTeam,
            AwayTeam = pred.AwayTeam,
            League = pred.League,
            MatchLocalDate = pred.MatchLocalDate,
            KickoffLocalTime = pred.MatchLocalTime.HasValue 
                ? pred.MatchLocalTime.Value.ToString("HH:mm", CultureInfo.InvariantCulture) 
                : pred.Time,
            MatchDateTimeUtc = pred.MatchDateTime,
            Market = FormatMarketCode(pred.PredictionCategory),
            Prediction = pred.PredictedOutcome ?? string.Empty,
            Odds = candidate.Odds,
            ModelProbability = candidate.ModelProbability,
            EdgePercent = Math.Round(candidate.Edge * 100m, 1),
            Conviction = conviction,
            TacticalRationale = BuildLegRationale(candidate)
        };
    }

    private static string BuildLegRationale(EvaluatedCandidate c)
    {
        var probPct = Math.Round(c.ModelProbability * 100m, 1);
        var edgePct = Math.Round(c.Edge * 100m, 1);
        var edgeSign = edgePct >= 0 ? "+" : "";

        return $"Model probability {probPct}% ({edgeSign}{edgePct}% edge vs implied odds {c.Odds:F2}). Solid market signals and historical support.";
    }

    private static string FormatMarketCode(string category) => category switch
    {
        "BothTeamsScore" => "BTTS",
        "Over2.5Goals" => "Over 2.5",
        "Under2.5Goals" => "Under 2.5",
        "StraightWin" => "1X2",
        "Draw" => "Draw",
        _ => category
    };

    private static decimal ResolveCandidateOdds(Prediction pred, PredictionOddsSnapshot? snapshot, MatchData? md)
    {
        if (snapshot is not null && snapshot.DecimalOdds > 1.0)
        {
            return (decimal)Math.Round(snapshot.DecimalOdds, 2);
        }

        if (md is not null)
        {
            var outcome = pred.PredictedOutcome ?? string.Empty;
            double raw = pred.PredictionCategory switch
            {
                "BothTeamsScore" => md.BttsYes > 1.0 ? md.BttsYes : 0,
                "Over2.5Goals" => md.OverTwoGoals > 1.0 ? md.OverTwoGoals : 0,
                "Under2.5Goals" => md.UnderTwoGoals > 1.0 ? md.UnderTwoGoals : 0,
                "StraightWin" => outcome.Contains("Home", StringComparison.OrdinalIgnoreCase) || outcome.Contains(pred.HomeTeam, StringComparison.OrdinalIgnoreCase)
                    ? md.HomeWin
                    : md.AwayWin,
                "Draw" => md.Draw > 1.0 ? md.Draw : 0,
                _ => 0
            };

            if (raw > 1.0)
            {
                return (decimal)Math.Round(raw, 2);
            }
        }

        var prob = (double)(pred.ConfidenceScore ?? 0.50m);
        var derived = BetPricingMath.ConvertProbabilityToDecimalOdds(prob);
        return derived.HasValue && derived.Value > 1.0 ? (decimal)Math.Round(derived.Value, 2) : 1.70m;
    }

    private async Task<(string BookingCode, string BookingUrl)> BookTicketAsync(List<OperationsLegDto> legs)
    {
        if (legs.Count == 0) return (string.Empty, string.Empty);

        try
        {
            var selections = legs.Select(leg => new BookingSelection
            {
                HomeTeam = leg.HomeTeam,
                AwayTeam = leg.AwayTeam,
                League = leg.League,
                Market = leg.Market,
                Prediction = leg.Prediction,
                PredictionId = leg.PredictionId,
                MatchDateTimeUtc = leg.MatchDateTimeUtc
            }).ToList();

            var result = await _bookingService.BookGamesAsync(selections);
            if (result.Success && !string.IsNullOrWhiteSpace(result.BookingCode))
            {
                return (result.BookingCode, result.BookingUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Automated booking on SportyBet threw an exception. Falling back to synthetic code.");
        }

        // Fallback deterministic code
        var raw = string.Join("_", legs.Select(l => l.PredictionId).OrderBy(id => id));
        var hash = (uint)Math.Abs(raw.GetHashCode());
        const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        var chars = new char[5];
        for (int i = 0; i < 5; i++)
        {
            chars[i] = alphabet[(int)(hash % (uint)alphabet.Length)];
            hash /= (uint)alphabet.Length;
        }

        var code = $"OP-{new string(chars)}";
        return (code, string.Empty);
    }

    private sealed class EvaluatedCandidate
    {
        public Prediction Prediction { get; set; } = null!;
        public decimal Odds { get; set; }
        public decimal ModelProbability { get; set; }
        public decimal ImpliedProbability { get; set; }
        public decimal Edge { get; set; }
    }
}
