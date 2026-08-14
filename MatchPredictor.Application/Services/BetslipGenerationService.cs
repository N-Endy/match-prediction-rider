using Hangfire;
using MatchPredictor.Application.Helpers;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Statistics;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MatchPredictor.Application.Services;

public sealed class BetslipGenerationService : IBetslipGenerationService
{
    private const string JobResource = "matchpredictor-betslips";
    public const int BankerSlipNumber = 0;
    public const string BankerTierLabel = "Banker (5-10x)";

    private static readonly string[] MainCategories =
    [
        "StraightWin",
        "BothTeamsScore",
        "Over2.5Goals",
        "Under2.5Goals"
    ];

    private readonly ApplicationDbContext _dbContext;
    private readonly ISportyBetBookingService _bookingService;
    private readonly ISourceMarketPricingService _pricingService;
    private readonly IAiAdvisorService _aiAdvisorService;
    private readonly BetslipSettings _settings;
    private readonly double _minimumEdge;
    private readonly ILogger<BetslipGenerationService> _logger;

    public BetslipGenerationService(
        ApplicationDbContext dbContext,
        ISportyBetBookingService bookingService,
        ISourceMarketPricingService pricingService,
        IAiAdvisorService aiAdvisorService,
        IOptions<BetslipSettings> options,
        ILogger<BetslipGenerationService> logger,
        IOptions<PredictionSettings>? predictionOptions = null)
    {
        _dbContext = dbContext;
        _bookingService = bookingService;
        _pricingService = pricingService;
        _aiAdvisorService = aiAdvisorService;
        _settings = options.Value;
        _logger = logger;
        _minimumEdge = predictionOptions?.Value.ValueBetMinimumEdge ?? 0.03;
    }

    [AutomaticRetry(OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(JobResource, 3600)]
    public async Task GenerateDailyBetslipsAsync(string? runLabel = null)
    {
        var settings = _settings;
        var today = DateTimeProvider.GetLocalDate();
        var nowUtc = DateTime.UtcNow;
        var normalizedRunLabel = NormalizeRunLabel(runLabel, DateTimeProvider.GetLocalTime());
        var dayKind = today.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
            ? BetslipDayKinds.Weekend
            : BetslipDayKinds.Weekday;

        try
        {
            var kickoffCutoff = nowUtc.AddMinutes(settings.MinMinutesBeforeKickoff);
            var predictions = await LoadTodayPredictionsAsync(today, kickoffCutoff);
            var oddsByPredictionId = await LoadPublishOddsAsync(predictions.Select(p => p.Id).ToList());

            _logger.LogInformation(
                "Betslip generation started for {Date} ({DayKind}/{RunLabel}): {PredictionCount} published predictions past kickoff cutoff ({CutoffMinutes} min).",
                today,
                dayKind,
                normalizedRunLabel,
                predictions.Count,
                settings.MinMinutesBeforeKickoff);

            var composed = new List<ComposedBetslip>();
            IReadOnlyList<SourceMarketFixture> sourceFixtures = [];
            try
            {
                sourceFixtures = await _pricingService.GetTodaySourceMarketFixturesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Live SportyBet pricing unavailable; banker and ladder slips will be skipped.");
            }

            var banker = await ComposeBankerSlipAsync(predictions, settings, sourceFixtures);
            if (banker is not null)
            {
                composed.Add(banker);
            }

            var usedFixtureKeys = CollectFixtureKeys(composed);
            var ladderPool = BuildLivePricedLadderPool(predictions, sourceFixtures);
            var ladderBeforeExclusion = ladderPool.Count;
            ladderPool = ExcludeUsedFixtures(ladderPool, usedFixtureKeys);
            var ladderRemoved = ladderBeforeExclusion - ladderPool.Count;
            var bands = dayKind == BetslipDayKinds.Weekend
                ? WeekendPayoutSlipComposer.BuildWeekendPlan(settings)
                : WeekendPayoutSlipComposer.BuildWeekdayPlan(settings);

            _logger.LogInformation(
                "Ladder pool after banker exclusion: {PoolCount} candidates ({RemovedCount} removed).",
                ladderPool.Count,
                ladderRemoved);

            _logger.LogInformation(
                "Ladder pool ready: {PoolCount} live-priced candidates for {BandCount} band(s).",
                ladderPool.Count,
                bands.Count);

            var ladderSlips = WeekendPayoutSlipComposer.Compose(
                ladderPool,
                bands,
                settings.MaxSlipsPerPrediction,
                settings.MaxSingleMarketShare,
                settings.OverlapPenalty);
            composed.AddRange(ladderSlips);
            AddFixtureKeys(usedFixtureKeys, ladderSlips);

            var omittedBands = bands
                .Where(b => ladderSlips.All(s => s.SlipNumber != b.SlipNumber))
                .Select(b => b.Title)
                .ToList();
            if (ladderSlips.Count > 0)
            {
                var builtSummary = string.Join(
                    "; ",
                    ladderSlips.Select(s =>
                        $"{s.Title} {s.Selections.Count} legs {(s.TargetCombinedOdds is double odds ? $"{odds:0.##}x" : "n/a")}"));
                _logger.LogInformation("Ladder bands composed: {BuiltSummary}.", builtSummary);
            }

            if (omittedBands.Count > 0)
            {
                _logger.LogInformation(
                    "Ladder bands omitted (could not hit odds band): {Omitted}.",
                    string.Join(", ", omittedBands));
            }
            else if (bands.Count > 0 && ladderSlips.Count == 0)
            {
                _logger.LogInformation("Ladder bands omitted: none of the {BandCount} band(s) could be packed.", bands.Count);
            }

            var drawSlip = (ComposedBetslip?)null;
            if (dayKind == BetslipDayKinds.Weekend)
            {
                drawSlip = await ComposeDrawSlipAsync(predictions, oddsByPredictionId, settings, usedFixtureKeys, sourceFixtures);
                if (drawSlip is not null)
                {
                    composed.Add(drawSlip);
                }
            }

            var betslipSet = new BetslipSet
            {
                SlipLocalDate = today,
                GeneratedAtUtc = nowUtc,
                RunLabel = normalizedRunLabel,
                DayKind = dayKind,
                IsCurrent = true,
                SlipCount = 0,
                Slips = []
            };

            foreach (var slip in composed.Where(s => s.Selections.Count > 0))
            {
                var entity = await BookAndBuildSlipAsync(slip, settings);
                betslipSet.Slips.Add(entity);

                var bookedCount = entity.Selections.Count(s => s.WasBooked);
                _logger.LogInformation(
                    "Booked slip {SlipNumber} '{Title}': {Status} ({BookedCount}/{SelectionCount}), code={CodePresent}.",
                    entity.SlipNumber,
                    entity.Title,
                    entity.BookingStatus,
                    bookedCount,
                    entity.SelectionCount,
                    string.IsNullOrWhiteSpace(entity.BookingCode) ? "absent" : "present");
            }

            betslipSet.SlipCount = betslipSet.Slips.Count;

            var previousSets = await _dbContext.BetslipSets
                .Where(s => s.SlipLocalDate == today && s.IsCurrent)
                .ToListAsync();
            foreach (var previous in previousSets)
            {
                previous.IsCurrent = false;
            }

            await _dbContext.BetslipSets.AddAsync(betslipSet);
            await _dbContext.SaveChangesAsync();

            var hasBanker = betslipSet.Slips.Any(s => s.SlipNumber == BankerSlipNumber);
            var ladderPersisted = betslipSet.Slips.Count(s =>
                s.SlipNumber != BankerSlipNumber &&
                !string.Equals(s.TierLabel, "AI Draws (5)", StringComparison.Ordinal));
            var hasDraws = betslipSet.Slips.Any(s =>
                string.Equals(s.TierLabel, "AI Draws (5)", StringComparison.Ordinal));

            _logger.LogInformation(
                "Betslip generation finished: {SlipCount} slips persisted (banker={HasBanker}, ladder={LadderCount}, draws={HasDraws}).",
                betslipSet.SlipCount,
                hasBanker ? "yes" : "no",
                ladderPersisted,
                hasDraws ? "yes" : "no");

            await LogStatusAsync(
                "Success",
                $"Generated {betslipSet.SlipCount} betslips for {today:yyyy-MM-dd} ({dayKind}/{normalizedRunLabel}).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Betslip generation failed for {Date}.", today);
            await LogStatusAsync("Failed", ex.Message);
            throw;
        }
    }

    private async Task<List<Prediction>> LoadTodayPredictionsAsync(DateOnly today, DateTime kickoffCutoffUtc)
    {
        return await _dbContext.Predictions
            .AsNoTracking()
            .Where(p =>
                p.MatchLocalDate == today &&
                p.IsCurrentRevision &&
                p.WasPublished &&
                (p.MatchDateTime == null || p.MatchDateTime > kickoffCutoffUtc))
            .ToListAsync();
    }

    private async Task<Dictionary<int, double>> LoadPublishOddsAsync(IReadOnlyList<int> predictionIds)
    {
        if (predictionIds.Count == 0)
        {
            return new Dictionary<int, double>();
        }

        var snapshots = await _dbContext.PredictionOddsSnapshots
            .AsNoTracking()
            .Where(s =>
                predictionIds.Contains(s.PredictionId) &&
                s.SnapshotKind == PredictionOddsSnapshotKind.Publish &&
                s.DecimalOdds > 1)
            .OrderByDescending(s => s.CapturedAtUtc)
            .ToListAsync();

        return snapshots
            .GroupBy(s => s.PredictionId)
            .ToDictionary(g => g.Key, g => g.First().DecimalOdds);
    }

    private async Task<ComposedBetslip?> ComposeBankerSlipAsync(
        IReadOnlyList<Prediction> predictions,
        BetslipSettings settings,
        IReadOnlyList<SourceMarketFixture> fixtures)
    {
        if (fixtures.Count == 0)
        {
            _logger.LogInformation("Banker slip skipped: no live SportyBet fixtures for today.");
            return null;
        }

        var livePriced = new List<(Prediction Prediction, BetslipComposerCandidate Candidate)>();
        foreach (var prediction in predictions.Where(p => MainCategories.Contains(p.PredictionCategory)))
        {
            var confidence = prediction.ConfidenceScore ?? prediction.RawConfidenceScore ?? 0m;
            if ((double)confidence < settings.BankerMinConfidence)
            {
                continue;
            }

            var fixture = SourceMarketFixtureMatcher.FindBestFixture(
                fixtures,
                prediction.HomeTeam,
                prediction.AwayTeam,
                prediction.League,
                prediction.MatchDateTime);

            if (!TryGetStakeableOdds(prediction, fixture, out var liveOdds))
            {
                continue;
            }

            var baseCandidate = ToCandidate(prediction, new Dictionary<int, double>());
            livePriced.Add((prediction, new BetslipComposerCandidate
            {
                PredictionId = baseCandidate.PredictionId,
                FixtureKey = baseCandidate.FixtureKey,
                League = baseCandidate.League,
                HomeTeam = baseCandidate.HomeTeam,
                AwayTeam = baseCandidate.AwayTeam,
                Market = baseCandidate.Market,
                PredictedOutcome = baseCandidate.PredictedOutcome,
                PredictionCategory = baseCandidate.PredictionCategory,
                Confidence = confidence,
                MatchDateTimeUtc = baseCandidate.MatchDateTimeUtc,
                DecimalOdds = liveOdds
            }));
        }

        _logger.LogInformation(
            "Banker pricing: {LivePricedCount} live-priced candidates from {FixtureCount} SportyBet fixtures (min confidence {MinConfidence:0.##}).",
            livePriced.Count,
            fixtures.Count,
            settings.BankerMinConfidence);

        // One pick per fixture in the shortlist — keep highest confidence.
        var shortlist = livePriced
            .GroupBy(x => x.Candidate.FixtureKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Candidate.Confidence).ThenBy(x => x.Candidate.PredictionId).First())
            .OrderByDescending(x => x.Candidate.Confidence)
            .ThenBy(x => x.Candidate.PredictionId)
            .Take(Math.Max(1, settings.BankerShortlistSize))
            .ToList();

        if (shortlist.Count == 0)
        {
            _logger.LogInformation(
                "Banker skipped: no live-priced candidates at/above min confidence {MinConfidence:0.##}.",
                settings.BankerMinConfidence);
            return null;
        }

        var deterministic = BankerSlipComposer.Compose(
            shortlist.Select(x => x.Candidate).ToList(),
            settings.BankerMinOdds,
            settings.BankerMaxOdds,
            settings.BankerFallbackMinOdds,
            settings.BankerFallbackMaxOdds,
            settings.BankerMaxPicks);

        if (deterministic.IsEmpty)
        {
            _logger.LogInformation(
                "Banker skipped: no combination in {MinOdds:0.##}-{MaxOdds:0.##}x (fallback {FallbackMin:0.##}-{FallbackMax:0.##}x) from {ShortlistCount} shortlist picks.",
                settings.BankerMinOdds,
                settings.BankerMaxOdds,
                settings.BankerFallbackMinOdds,
                settings.BankerFallbackMaxOdds,
                shortlist.Count);
            return null;
        }

        var signalByPredictionId = await LoadSignalSummariesAsync(shortlist.Select(x => x.Prediction).ToList());
        var aiRequests = shortlist.Select(x =>
        {
            signalByPredictionId.TryGetValue(x.Prediction.Id, out var signal);
            return new BankerPickRequest
            {
                PredictionId = x.Candidate.PredictionId,
                League = x.Candidate.League,
                HomeTeam = x.Candidate.HomeTeam,
                AwayTeam = x.Candidate.AwayTeam,
                Market = x.Candidate.Market,
                PredictedOutcome = x.Candidate.PredictedOutcome,
                Confidence = x.Candidate.Confidence,
                DecimalOdds = x.Candidate.DecimalOdds ?? 0d,
                MatchDateTimeUtc = x.Candidate.MatchDateTimeUtc,
                SignalSummary = signal?.Summary,
                AllSignalsAlign = signal?.AllSignalsAlign,
                ModelDivergesFromBookmaker = signal?.ModelDivergesFromBookmaker
            };
        }).ToList();

        var activeMin = deterministic.ActiveMinOdds;
        var activeMax = deterministic.ActiveMaxOdds;
        var aiVetted = false;
        var riskNote = string.Empty;
        var selected = deterministic.Selections.ToList();
        var shortfallNotes = new List<string>();

        if (deterministic.UsedFallbackRange)
        {
            shortfallNotes.Add($"Widened banker odds range to {activeMin:0.##}-{activeMax:0.##}x.");
        }

        try
        {
            var aiResult = await _aiAdvisorService.SelectBankerPicksAsync(aiRequests, activeMin, activeMax);
            var validated = TryValidateBankerAiPicks(
                aiResult,
                shortlist.Select(x => x.Candidate).ToList(),
                activeMin,
                activeMax,
                settings.BankerMaxPicks);

            if (validated is not null)
            {
                selected = validated.Value.Selections.ToList();
                riskNote = validated.Value.RiskNote;
                aiVetted = true;
            }
            else
            {
                shortfallNotes.Add("AI banker selection invalid or unavailable; using deterministic composer (not AI-vetted).");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Banker AI selection failed; using deterministic composer.");
            shortfallNotes.Add("AI banker selection failed; using deterministic composer (not AI-vetted).");
        }

        if (!aiVetted && string.IsNullOrWhiteSpace(riskNote))
        {
            riskNote = "Deterministic high-confidence banker. Not AI-vetted.";
        }

        var combined = BankerSlipComposer.CalculateCombinedOdds(selected);
        _logger.LogInformation(
            "Banker composed: {Legs} legs, {CombinedOdds:0.##}x ({RangeKind} range {MinOdds:0.##}-{MaxOdds:0.##}x), AI vetted={AiVetted}.",
            selected.Count,
            combined,
            deterministic.UsedFallbackRange ? "fallback" : "primary",
            activeMin,
            activeMax,
            aiVetted);

        return new ComposedBetslip
        {
            SlipNumber = BankerSlipNumber,
            Title = "Banker of the Day",
            TierLabel = BankerTierLabel,
            TargetMinSelections = 1,
            TargetMaxSelections = settings.BankerMaxPicks,
            Selections = selected,
            ShortfallNote = shortfallNotes.Count > 0 ? string.Join(" ", shortfallNotes) : null,
            AiSummary = string.IsNullOrWhiteSpace(riskNote)
                ? "High-stakes banker verified against live SportyBet odds."
                : riskNote,
            TargetCombinedOdds = combined,
            IsBanker = true,
            ActiveMinOdds = activeMin,
            ActiveMaxOdds = activeMax
        };
    }

    private static (IReadOnlyList<BetslipComposerCandidate> Selections, string RiskNote)? TryValidateBankerAiPicks(
        BankerPickResult aiResult,
        IReadOnlyList<BetslipComposerCandidate> shortlist,
        double minOdds,
        double maxOdds,
        int maxPicks)
    {
        if (aiResult.Picks.Count == 0)
        {
            return null;
        }

        var byId = shortlist.ToDictionary(c => c.PredictionId);
        var selected = new List<BetslipComposerCandidate>();
        var usedFixtures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pick in aiResult.Picks.Take(Math.Max(1, maxPicks)))
        {
            if (!byId.TryGetValue(pick.PredictionId, out var candidate))
            {
                return null;
            }

            var fixtureKey = string.IsNullOrWhiteSpace(candidate.FixtureKey)
                ? $"{candidate.League}|{candidate.HomeTeam}|{candidate.AwayTeam}"
                : candidate.FixtureKey;

            if (!usedFixtures.Add(fixtureKey))
            {
                return null;
            }

            selected.Add(new BetslipComposerCandidate
            {
                PredictionId = candidate.PredictionId,
                FixtureKey = candidate.FixtureKey,
                League = candidate.League,
                HomeTeam = candidate.HomeTeam,
                AwayTeam = candidate.AwayTeam,
                Market = candidate.Market,
                PredictedOutcome = candidate.PredictedOutcome,
                PredictionCategory = candidate.PredictionCategory,
                Confidence = candidate.Confidence,
                MatchDateTimeUtc = candidate.MatchDateTimeUtc,
                DecimalOdds = candidate.DecimalOdds,
                AiNote = string.IsNullOrWhiteSpace(pick.Reason) ? null : pick.Reason.Trim()
            });
        }

        var combined = BankerSlipComposer.CalculateCombinedOdds(selected);
        if (!BankerSlipComposer.IsWithinOddsRange(combined, minOdds, maxOdds))
        {
            return null;
        }

        return (selected, aiResult.RiskNote?.Trim() ?? string.Empty);
    }

    private async Task<Dictionary<int, SignalPayload>> LoadSignalSummariesAsync(IReadOnlyList<Prediction> predictions)
    {
        var result = new Dictionary<int, SignalPayload>();
        if (predictions.Count == 0)
        {
            return result;
        }

        var fixtureKeys = predictions
            .Select(p => p.FixtureKey)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (fixtureKeys.Count == 0)
        {
            return result;
        }

        var observations = await _dbContext.ForecastObservations
            .AsNoTracking()
            .Where(o => o.IsCurrentRevision && fixtureKeys.Contains(o.FixtureKey))
            .ToListAsync();

        foreach (var prediction in predictions)
        {
            if (!PredictionMarketExtensions.TryFromCategory(prediction.PredictionCategory, out var market))
            {
                continue;
            }

            // StraightWin stores Home/Away as separate enum values on observations.
            var observation = observations.FirstOrDefault(o =>
                string.Equals(o.FixtureKey, prediction.FixtureKey, StringComparison.OrdinalIgnoreCase) &&
                MarketsAlign(o.Market, market, prediction.PredictedOutcome) &&
                string.Equals(o.PredictedOutcome, prediction.PredictedOutcome, StringComparison.OrdinalIgnoreCase));

            if (observation is null)
            {
                continue;
            }

            var breakdown = SignalBreakdownParser.TryParse(
                observation.FeatureContributionsJson,
                prediction.PredictionCategory,
                prediction.PredictedOutcome,
                observation.CalibratedProbability);

            if (breakdown is null)
            {
                continue;
            }

            result[prediction.Id] = new SignalPayload(
                breakdown.SignalAgreement.Summary,
                breakdown.SignalAgreement.AllSignalsAlign,
                breakdown.SignalAgreement.ModelDivergesFromBookmaker);
        }

        return result;
    }

    private static bool MarketsAlign(PredictionMarket observed, PredictionMarket predictedCategory, string predictedOutcome)
    {
        if (observed == predictedCategory)
        {
            return true;
        }

        if (predictedCategory != PredictionMarket.StraightWin)
        {
            return false;
        }

        return observed switch
        {
            PredictionMarket.HomeWin => predictedOutcome.Contains("Home", StringComparison.OrdinalIgnoreCase),
            PredictionMarket.AwayWin => predictedOutcome.Contains("Away", StringComparison.OrdinalIgnoreCase),
            PredictionMarket.StraightWin => true,
            _ => false
        };
    }

    private async Task<ComposedBetslip?> ComposeDrawSlipAsync(
        IReadOnlyList<Prediction> predictions,
        IReadOnlyDictionary<int, double> oddsByPredictionId,
        BetslipSettings settings,
        IReadOnlySet<string> usedFixtureKeys,
        IReadOnlyList<SourceMarketFixture> sourceFixtures)
    {
        var drawPool = predictions
            .Where(p => p.PredictionCategory == "Draw")
            .OrderByDescending(p => p.ConfidenceScore ?? p.RawConfidenceScore ?? 0m)
            .ThenBy(p => p.Id)
            .ToList();

        var drawBeforeExclusion = drawPool.Count;
        drawPool = drawPool
            .Where(p => !IsFixtureUsed(ResolveFixtureKey(p), usedFixtureKeys))
            .Where(p =>
            {
                var fixture = SourceMarketFixtureMatcher.FindBestFixture(
                    sourceFixtures,
                    p.HomeTeam,
                    p.AwayTeam,
                    p.League,
                    p.MatchDateTime);
                return TryGetStakeableOdds(p, fixture, out _);
            })
            .ToList();
        var drawRemoved = drawBeforeExclusion - drawPool.Count;

        var drawCandidates = drawPool
            .Take(Math.Max(settings.DrawCandidatePoolSize, settings.DrawSlipSize))
            .ToList();

        if (drawCandidates.Count == 0)
        {
            _logger.LogInformation(
                "AI Draws skipped: no published Draw candidates for today ({RemovedCount} excluded as already used).",
                drawRemoved);
            return null;
        }

        _logger.LogInformation(
            "AI Draws pool: {CandidateCount} draw candidate(s) after excluding {RemovedCount} used fixture(s); targeting {DrawSlipSize} pick(s).",
            drawCandidates.Count,
            drawRemoved,
            settings.DrawSlipSize);

        var requests = drawCandidates.Select(p => new BetslipDrawPickRequest
        {
            PredictionId = p.Id,
            League = p.League,
            HomeTeam = p.HomeTeam,
            AwayTeam = p.AwayTeam,
            Confidence = p.ConfidenceScore ?? p.RawConfidenceScore ?? 0m,
            MatchDateTimeUtc = p.MatchDateTime
        }).ToList();

        IReadOnlyList<BetslipDrawPickSelection> selected;
        try
        {
            selected = await _aiAdvisorService.SelectBestDrawPicksAsync(requests, settings.DrawSlipSize);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Draw-pick AI selection failed; using confidence ranking.");
            selected = requests
                .Take(settings.DrawSlipSize)
                .Select(r => new BetslipDrawPickSelection { PredictionId = r.PredictionId })
                .ToList();
        }

        var byId = drawCandidates.ToDictionary(p => p.Id);
        var picks = new List<BetslipComposerCandidate>();
        foreach (var pick in selected)
        {
            if (!byId.TryGetValue(pick.PredictionId, out var prediction))
            {
                continue;
            }

            var candidate = ToCandidate(prediction, oddsByPredictionId);
            picks.Add(new BetslipComposerCandidate
            {
                PredictionId = candidate.PredictionId,
                FixtureKey = candidate.FixtureKey,
                League = candidate.League,
                HomeTeam = candidate.HomeTeam,
                AwayTeam = candidate.AwayTeam,
                Market = candidate.Market,
                PredictedOutcome = candidate.PredictedOutcome,
                PredictionCategory = candidate.PredictionCategory,
                Confidence = candidate.Confidence,
                MatchDateTimeUtc = candidate.MatchDateTimeUtc,
                DecimalOdds = candidate.DecimalOdds,
                AiNote = string.IsNullOrWhiteSpace(pick.Reason) ? null : pick.Reason.Trim()
            });
        }

        if (picks.Count == 0)
        {
            picks = drawCandidates.Take(settings.DrawSlipSize).Select(p => ToCandidate(p, oddsByPredictionId)).ToList();
        }

        _logger.LogInformation("AI Draws composed: {PickCount} pick(s).", picks.Count);

        return new ComposedBetslip
        {
            SlipNumber = 11,
            Title = "AI Best Draws",
            TierLabel = "AI Draws (5)",
            TargetMinSelections = settings.DrawSlipSize,
            TargetMaxSelections = settings.DrawSlipSize,
            Selections = picks,
            ShortfallNote = picks.Count < settings.DrawSlipSize
                ? $"Only {picks.Count} draw picks available (target {settings.DrawSlipSize})."
                : null,
            AiSummary = "AI-selected best draw games for today."
        };
    }

    private async Task<Betslip> BookAndBuildSlipAsync(ComposedBetslip composed, BetslipSettings settings)
    {
        var selections = composed.Selections.Select(c => new BetslipSelection
        {
            PredictionId = c.PredictionId,
            League = c.League,
            HomeTeam = c.HomeTeam,
            AwayTeam = c.AwayTeam,
            Market = c.Market,
            PredictedOutcome = c.PredictedOutcome,
            ConfidenceScore = c.Confidence,
            MatchDateTimeUtc = c.MatchDateTimeUtc,
            DecimalOdds = c.DecimalOdds,
            AiNote = c.AiNote,
            WasBooked = false
        }).ToList();

        BookingResult bookingResult;
        try
        {
            var bookingSelections = selections.Select(s => new BookingSelection
            {
                PredictionId = s.PredictionId,
                HomeTeam = s.HomeTeam,
                AwayTeam = s.AwayTeam,
                League = s.League,
                Market = s.Market,
                Prediction = s.PredictedOutcome,
                MatchDateTimeUtc = s.MatchDateTimeUtc
            }).ToList();

            bookingResult = await _bookingService.BookGamesAsync(bookingSelections);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Booking failed for slip {SlipNumber} ({Title}).", composed.SlipNumber, composed.Title);
            bookingResult = new BookingResult
            {
                Success = false,
                Message = "SportyBet booking failed. Code unavailable until the next run."
            };
        }

        if (settings.BookingDelayMilliseconds > 0)
        {
            await Task.Delay(settings.BookingDelayMilliseconds);
        }

        var unresolvedIds = bookingResult.UnresolvedSelections
            .Where(u => u.PredictionId.HasValue)
            .Select(u => u.PredictionId!.Value)
            .ToHashSet();

        foreach (var selection in selections)
        {
            if (!bookingResult.Success || string.IsNullOrWhiteSpace(bookingResult.BookingCode))
            {
                selection.WasBooked = false;
                continue;
            }

            if (unresolvedIds.Count > 0)
            {
                selection.WasBooked = selection.PredictionId.HasValue &&
                                     !unresolvedIds.Contains(selection.PredictionId.Value);
            }
            else
            {
                selection.WasBooked = true;
            }
        }

        var bookedCount = selections.Count(s => s.WasBooked);
        string bookingStatus;
        if (bookingResult.Success && !string.IsNullOrWhiteSpace(bookingResult.BookingCode))
        {
            bookingStatus = bookingResult.SkippedCount > 0 || unresolvedIds.Count > 0 || bookedCount < selections.Count
                ? BetslipBookingStatuses.Partial
                : BetslipBookingStatuses.Booked;
        }
        else
        {
            bookingStatus = BetslipBookingStatuses.Failed;
        }

        var statusParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(composed.ShortfallNote))
        {
            statusParts.Add(composed.ShortfallNote);
        }

        if (!string.IsNullOrWhiteSpace(bookingResult.Message))
        {
            statusParts.Add(bookingResult.Message);
        }

        if (bookingStatus == BetslipBookingStatuses.Failed)
        {
            statusParts.Add("Booking code unavailable; retrying next run.");
        }

        double? combinedOdds = composed.TargetCombinedOdds;
        var oddsValues = selections.Where(s => s.WasBooked && s.DecimalOdds is > 1).Select(s => s.DecimalOdds!.Value).ToList();
        if (oddsValues.Count > 0 && oddsValues.Count == bookedCount)
        {
            combinedOdds = oddsValues.Aggregate(1d, (acc, odds) => acc * odds);
        }
        else if (oddsValues.Count > 0)
        {
            // Partial booking: recompute from booked picks only.
            combinedOdds = oddsValues.Aggregate(1d, (acc, odds) => acc * odds);
        }

        if (composed.IsBanker &&
            combinedOdds is > 1 &&
            composed.ActiveMinOdds is double bankerMin &&
            composed.ActiveMaxOdds is double bankerMax &&
            !BankerSlipComposer.IsWithinOddsRange(combinedOdds.Value, bankerMin, bankerMax))
        {
            statusParts.Add(
                $"After booking skips, total odds {combinedOdds.Value:0.00}x fell outside the {bankerMin:0.##}-{bankerMax:0.##}x banker range.");
        }

        if (composed.IsPayoutBand &&
            combinedOdds is > 1 &&
            composed.ActiveMinOdds is double bandMin &&
            composed.ActiveMaxOdds is double bandMax &&
            !BankerSlipComposer.IsWithinOddsRange(combinedOdds.Value, bandMin, bandMax))
        {
            statusParts.Add(
                $"After booking skips, total odds {combinedOdds.Value:0.00}x fell outside the {bandMin:0.##}-{bandMax:0.##}x payout band.");
        }

        return new Betslip
        {
            SlipNumber = composed.SlipNumber,
            Title = composed.Title,
            TierLabel = composed.TierLabel,
            TargetMinSelections = composed.TargetMinSelections,
            TargetMaxSelections = composed.TargetMaxSelections,
            SelectionCount = selections.Count,
            BookingCode = bookingResult.BookingCode ?? string.Empty,
            BookingUrl = bookingResult.BookingUrl ?? string.Empty,
            BookingStatus = bookingStatus,
            StatusMessage = string.Join(" ", statusParts).Trim(),
            EarliestKickoffUtc = selections
                .Where(s => s.MatchDateTimeUtc.HasValue)
                .Select(s => (DateTime?)s.MatchDateTimeUtc!.Value)
                .DefaultIfEmpty(null)
                .Min(),
            CombinedDecimalOdds = combinedOdds,
            AiSummary = composed.AiSummary,
            Selections = selections
        };
    }

    private List<BetslipComposerCandidate> BuildLivePricedLadderPool(
        IReadOnlyList<Prediction> predictions,
        IReadOnlyList<SourceMarketFixture> fixtures)
    {
        if (fixtures.Count == 0)
        {
            _logger.LogInformation("Ladder slips: no live SportyBet fixtures for today.");
            return [];
        }

        var livePriced = new List<BetslipComposerCandidate>();
        foreach (var prediction in predictions.Where(p => MainCategories.Contains(p.PredictionCategory)))
        {
            var fixture = SourceMarketFixtureMatcher.FindBestFixture(
                fixtures,
                prediction.HomeTeam,
                prediction.AwayTeam,
                prediction.League,
                prediction.MatchDateTime);

            if (!TryGetStakeableOdds(prediction, fixture, out var liveOdds))
            {
                continue;
            }

            var baseCandidate = ToCandidate(prediction, new Dictionary<int, double>());
            livePriced.Add(new BetslipComposerCandidate
            {
                PredictionId = baseCandidate.PredictionId,
                FixtureKey = baseCandidate.FixtureKey,
                League = baseCandidate.League,
                HomeTeam = baseCandidate.HomeTeam,
                AwayTeam = baseCandidate.AwayTeam,
                Market = baseCandidate.Market,
                PredictedOutcome = baseCandidate.PredictedOutcome,
                PredictionCategory = baseCandidate.PredictionCategory,
                Confidence = baseCandidate.Confidence,
                MatchDateTimeUtc = baseCandidate.MatchDateTimeUtc,
                DecimalOdds = liveOdds
            });
        }

        // One pick per fixture — keep highest confidence.
        return livePriced
            .GroupBy(c => c.FixtureKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(c => c.Confidence).ThenBy(c => c.PredictionId).First())
            .OrderByDescending(c => c.Confidence)
            .ThenBy(c => c.PredictionId)
            .ToList();
    }

    private bool TryGetStakeableOdds(
        Prediction prediction,
        SourceMarketFixture? fixture,
        out double liveOdds)
    {
        liveOdds = 0d;
        if (!MarketQuoteResolver.TryResolveStakeableQuote(prediction, fixture, storedMatch: null, out var quote))
        {
            return false;
        }

        var modelProbability = (double)(prediction.ConfidenceScore ?? prediction.RawConfidenceScore ?? 0m);
        if (!BetPricingMath.MeetsMinimumEdge(modelProbability, quote.MarketProbability, _minimumEdge))
        {
            return false;
        }

        liveOdds = quote.DecimalOdds;
        return liveOdds > 1d;
    }

    private static HashSet<string> CollectFixtureKeys(IEnumerable<ComposedBetslip> slips)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddFixtureKeys(keys, slips);
        return keys;
    }

    private static void AddFixtureKeys(HashSet<string> keys, IEnumerable<ComposedBetslip> slips)
    {
        foreach (var slip in slips)
        {
            foreach (var selection in slip.Selections)
            {
                if (!string.IsNullOrWhiteSpace(selection.FixtureKey))
                {
                    keys.Add(selection.FixtureKey);
                }
            }
        }
    }

    private static List<BetslipComposerCandidate> ExcludeUsedFixtures(
        IReadOnlyList<BetslipComposerCandidate> candidates,
        IReadOnlySet<string> usedFixtureKeys)
    {
        if (usedFixtureKeys.Count == 0)
        {
            return candidates.ToList();
        }

        return candidates
            .Where(c => !IsFixtureUsed(c.FixtureKey, usedFixtureKeys))
            .ToList();
    }

    private static bool IsFixtureUsed(string? fixtureKey, IReadOnlySet<string> usedFixtureKeys) =>
        !string.IsNullOrWhiteSpace(fixtureKey) && usedFixtureKeys.Contains(fixtureKey);

    private static string ResolveFixtureKey(Prediction prediction) =>
        string.IsNullOrWhiteSpace(prediction.FixtureKey)
            ? $"{prediction.League}|{prediction.HomeTeam}|{prediction.AwayTeam}|{prediction.MatchLocalDate}"
            : prediction.FixtureKey;

    private static BetslipComposerCandidate ToCandidate(
        Prediction prediction,
        IReadOnlyDictionary<int, double> oddsByPredictionId)
    {
        oddsByPredictionId.TryGetValue(prediction.Id, out var odds);
        return new BetslipComposerCandidate
        {
            PredictionId = prediction.Id,
            FixtureKey = ResolveFixtureKey(prediction),
            League = prediction.League,
            HomeTeam = prediction.HomeTeam,
            AwayTeam = prediction.AwayTeam,
            Market = ToBookingMarket(prediction.PredictionCategory),
            PredictedOutcome = prediction.PredictedOutcome,
            PredictionCategory = prediction.PredictionCategory,
            Confidence = prediction.ConfidenceScore ?? prediction.RawConfidenceScore ?? 0m,
            MatchDateTimeUtc = prediction.MatchDateTime,
            DecimalOdds = odds > 1 ? odds : null
        };
    }

    private static string ToBookingMarket(string category) =>
        category switch
        {
            "BothTeamsScore" => "BTTS",
            "Over2.5Goals" => "Over2.5",
            "Under2.5Goals" => "Under2.5",
            "Draw" => "1X2",
            "StraightWin" => "StraightWin",
            _ => category
        };

    private static string NormalizeRunLabel(string? runLabel, DateTime localNow)
    {
        if (!string.IsNullOrWhiteSpace(runLabel))
        {
            var normalized = runLabel.Trim().ToLowerInvariant();
            if (normalized is BetslipRunLabels.Morning or BetslipRunLabels.Midday)
            {
                return normalized;
            }
        }

        return localNow.Hour < 12 ? BetslipRunLabels.Morning : BetslipRunLabels.Midday;
    }

    private async Task LogStatusAsync(string status, string message)
    {
        await _dbContext.ScrapingLogs.AddAsync(new ScrapingLog
        {
            EventName = ScrapingEventNames.BetslipGeneration,
            Timestamp = DateTime.UtcNow,
            Status = status,
            Message = message
        });
        await _dbContext.SaveChangesAsync();
    }

    private sealed record SignalPayload(string? Summary, bool AllSignalsAlign, bool ModelDivergesFromBookmaker);
}
