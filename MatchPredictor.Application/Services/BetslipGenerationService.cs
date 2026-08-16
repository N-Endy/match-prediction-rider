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
    public const int RolloverSlipNumber = 10;
    public const string RolloverTierLabel = "Rollover (1.20-1.50x)";
    public const string DrawsTierLabel = "AI Draws (5)";

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
    private readonly double _ladderMinimumEdge;
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
        _ladderMinimumEdge = Math.Clamp(_settings.LadderMinimumEdge, 0d, 1d);
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

            var universe = BuildLiveQuotedUniverse(predictions, sourceFixtures);
            var (ranked, aiPassedCount) = await ScreenLiveQuotedUniverseAsync(universe);
            var mainPool = CollapseOnePerFixture(
                ranked.Where(p => MainCategories.Contains(p.Candidate.PredictionCategory)).ToList());
            var drawPool = ranked
                .Where(p => string.Equals(p.Candidate.PredictionCategory, "Draw", StringComparison.OrdinalIgnoreCase))
                .ToList();

            LogComposeFunnel(universe, mainPool, aiPassedCount);

            var rollover = await ComposeRolloverFromPassersAsync(mainPool, settings);
            if (rollover is not null)
            {
                composed.Add(rollover);
            }

            var usedFixtureKeys = CollectFixtureKeys(composed);
            var bankerPassers = ExcludeUsedFixtures(
                FilterByCategoryAndEdge(mainPool, MainCategories, _minimumEdge),
                usedFixtureKeys);
            var banker = await ComposeBankerFromPassersAsync(bankerPassers, settings);
            if (banker is not null)
            {
                composed.Add(banker);
            }

            AddFixtureKeys(usedFixtureKeys, composed.Where(s => s.IsBanker));
            var bands = dayKind == BetslipDayKinds.Weekend
                ? WeekendPayoutSlipComposer.BuildWeekendPlan(settings)
                : WeekendPayoutSlipComposer.BuildWeekdayPlan(settings);

            var ladderPassers = ExcludeUsedFixtures(
                FilterByCategoryAndEdge(mainPool, MainCategories, _ladderMinimumEdge),
                usedFixtureKeys);

            _logger.LogInformation(
                "Ladder pool after exclusivity: {PoolCount} live-quoted picks ({RemovedCount} removed).",
                ladderPassers.Count,
                FilterByCategoryAndEdge(mainPool, MainCategories, _ladderMinimumEdge).Count - ladderPassers.Count);

            _logger.LogInformation(
                "Ladder pool ready: {PoolCount} live-quoted picks for {BandCount} band(s).",
                ladderPassers.Count,
                bands.Count);

            var ladderSlips = await ComposeLadderFromPassersAsync(ladderPassers, bands, settings);
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
                drawSlip = await ComposeDrawFromPassersAsync(
                    drawPool,
                    settings,
                    usedFixtureKeys);
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

            var hasRollover = betslipSet.Slips.Any(IsRolloverSlip);
            var hasBanker = betslipSet.Slips.Any(IsBankerSlip);
            var ladderPersisted = betslipSet.Slips.Count(IsLadderSlip);
            var hasDraws = betslipSet.Slips.Any(IsDrawSlip);

            _logger.LogInformation(
                "Betslip generation finished: {SlipCount} slips persisted (rollover={HasRollover}, banker={HasBanker}, ladder={LadderCount}, draws={HasDraws}).",
                betslipSet.SlipCount,
                hasRollover ? "yes" : "no",
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

    public static bool IsRolloverSlip(Betslip slip) =>
        slip.SlipNumber == RolloverSlipNumber ||
        slip.TierLabel.StartsWith("Rollover", StringComparison.OrdinalIgnoreCase);

    public static bool IsBankerSlip(Betslip slip) =>
        slip.SlipNumber == BankerSlipNumber ||
        slip.TierLabel.StartsWith("Banker", StringComparison.OrdinalIgnoreCase);

    public static bool IsDrawSlip(Betslip slip) =>
        string.Equals(slip.TierLabel, DrawsTierLabel, StringComparison.Ordinal);

    public static bool IsLadderSlip(Betslip slip) =>
        !IsRolloverSlip(slip) && !IsBankerSlip(slip) && !IsDrawSlip(slip);

    private async Task<ComposedBetslip?> ComposeRolloverFromPassersAsync(
        IReadOnlyList<LiveQuotedCandidate> mainPool,
        BetslipSettings settings)
    {
        var minOdds = settings.RolloverMinOdds;
        var maxOdds = settings.RolloverMaxOdds;
        var pool = FilterByCategoryAndEdge(mainPool, MainCategories, _minimumEdge)
            .Where(p => p.Candidate.DecimalOdds is double odds &&
                        BankerSlipComposer.IsWithinOddsRange(odds, minOdds, maxOdds))
            .OrderByDescending(p => p.Candidate.ResearchScore ?? (double)p.Candidate.Confidence)
            .ThenBy(p => p.Prediction.Id)
            .ToList();

        if (pool.Count == 0)
        {
            _logger.LogInformation(
                "Rollover skipped: no live-quoted main-market pick in {MinOdds:0.##}-{MaxOdds:0.##}x meeting the 3% edge floor.",
                minOdds,
                maxOdds);
            return null;
        }

        var shortlistSize = Math.Clamp(settings.RolloverShortlistSize, 1, 50);
        var shortlist = pool.Take(shortlistSize).ToList();

        _logger.LogInformation(
            "Rollover compose pool: {PoolCount} live-quoted shorts in {MinOdds:0.##}-{MaxOdds:0.##}x; shortlist={ShortlistCount}.",
            pool.Count,
            minOdds,
            maxOdds,
            shortlist.Count);

        var selected = shortlist[0].Candidate;
        var aiVetted = false;
        var riskNote = string.Empty;
        var shortfallNotes = new List<string>();

        try
        {
            var signalByPredictionId = await LoadSignalSummariesAsync(shortlist.Select(p => p.Prediction).ToList());
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
                    PredictionCategory = x.Candidate.PredictionCategory,
                    Confidence = x.Candidate.Confidence,
                    DecimalOdds = x.Candidate.DecimalOdds ?? 0d,
                    MatchDateTimeUtc = x.Candidate.MatchDateTimeUtc,
                    SignalSummary = signal?.Summary,
                    AllSignalsAlign = signal?.AllSignalsAlign,
                    ModelDivergesFromBookmaker = signal?.ModelDivergesFromBookmaker
                };
            }).ToList();

            var aiResult = await _aiAdvisorService.SelectRolloverPickAsync(aiRequests, minOdds, maxOdds);
            var validated = TryValidateRolloverAiPick(
                aiResult,
                shortlist.Select(p => p.Candidate).ToList(),
                minOdds,
                maxOdds);

            if (validated is not null)
            {
                selected = validated.Value.Selection;
                riskNote = validated.Value.RiskNote;
                aiVetted = true;
            }
            else
            {
                shortfallNotes.Add("AI rollover selection invalid or unavailable; using the top remaining short (not AI-vetted).");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rollover AI selection failed; using the top remaining short.");
            shortfallNotes.Add("AI rollover selection failed; using the top remaining short (not AI-vetted).");
        }

        if (!aiVetted && string.IsNullOrWhiteSpace(riskNote))
        {
            riskNote = "Single stack-it-all rollover. Not AI-vetted.";
        }

        var combined = selected.DecimalOdds ?? 0d;
        _logger.LogInformation(
            "Rollover composed: 1 leg, {CombinedOdds:0.##}x ({MinOdds:0.##}-{MaxOdds:0.##}x), AI vetted={AiVetted}.",
            combined,
            minOdds,
            maxOdds,
            aiVetted);

        var summary = string.IsNullOrWhiteSpace(riskNote)
            ? "Single stack-it-all pick — the whole bankroll rolls into the next bet."
            : $"Single stack-it-all pick. {riskNote}";

        return new ComposedBetslip
        {
            SlipNumber = RolloverSlipNumber,
            Title = "Rollover",
            TierLabel = RolloverTierLabel,
            TargetMinSelections = 1,
            TargetMaxSelections = 1,
            Selections = [selected],
            ShortfallNote = shortfallNotes.Count > 0 ? string.Join(" ", shortfallNotes) : null,
            AiSummary = summary,
            TargetCombinedOdds = combined,
            IsRollover = true,
            ActiveMinOdds = minOdds,
            ActiveMaxOdds = maxOdds
        };
    }

    private static (BetslipComposerCandidate Selection, string RiskNote)? TryValidateRolloverAiPick(
        BankerPickResult aiResult,
        IReadOnlyList<BetslipComposerCandidate> shortlist,
        double minOdds,
        double maxOdds)
    {
        if (aiResult.Picks.Count == 0)
        {
            return null;
        }

        var byId = shortlist.ToDictionary(c => c.PredictionId);
        if (!byId.TryGetValue(aiResult.Picks[0].PredictionId, out var candidate))
        {
            return null;
        }

        if (candidate.DecimalOdds is not double odds ||
            !BankerSlipComposer.IsWithinOddsRange(odds, minOdds, maxOdds))
        {
            return null;
        }

        return (WithAiNote(candidate, aiResult.Picks[0].Reason), aiResult.RiskNote?.Trim() ?? string.Empty);
    }

    private async Task<ComposedBetslip?> ComposeBankerFromPassersAsync(
        IReadOnlyList<LiveQuotedCandidate> passers,
        BetslipSettings settings)
    {
        if (passers.Count == 0)
        {
            _logger.LogInformation("Banker skipped: no live-quoted main-market picks meeting the 3% edge floor.");
            return null;
        }

        var eligible = passers
            .Select(p => p.Candidate)
            .OrderByDescending(c => c.ResearchScore ?? (double)c.Confidence)
            .ThenBy(c => c.PredictionId)
            .ToList();

        _logger.LogInformation(
            "Banker compose pool: {PasserCount} live-quoted picks meeting the 3% edge floor.",
            eligible.Count);

        var deterministic = BankerSlipComposer.Compose(
            eligible,
            settings.BankerMinOdds,
            settings.BankerMaxOdds,
            settings.BankerFallbackMinOdds,
            settings.BankerFallbackMaxOdds,
            settings.BankerMaxPicks);

        if (deterministic.IsEmpty)
        {
            _logger.LogInformation(
                "Banker skipped: no combination in {MinOdds:0.##}-{MaxOdds:0.##}x (fallback {FallbackMin:0.##}-{FallbackMax:0.##}x) from {EligibleCount} live-quoted picks.",
                settings.BankerMinOdds,
                settings.BankerMaxOdds,
                settings.BankerFallbackMinOdds,
                settings.BankerFallbackMaxOdds,
                eligible.Count);
            return null;
        }

        var signalByPredictionId = await LoadSignalSummariesAsync(passers.Select(p => p.Prediction).ToList());
        var aiRequests = passers.Select(x =>
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
                PredictionCategory = x.Candidate.PredictionCategory,
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
                eligible,
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

    private async Task<ComposedBetslip?> ComposeDrawFromPassersAsync(
        IReadOnlyList<LiveQuotedCandidate> passers,
        BetslipSettings settings,
        IReadOnlySet<string> usedFixtureKeys)
    {
        var drawPassers = ExcludeUsedFixtures(
            FilterByCategoryAndEdge(passers, ["Draw"], _minimumEdge),
            usedFixtureKeys);

        if (drawPassers.Count == 0)
        {
            _logger.LogInformation("AI Draws skipped: no live-quoted Draw picks remaining after exclusivity and 3% edge.");
            return null;
        }

        _logger.LogInformation(
            "AI Draws pool: {CandidateCount} live-quoted draw pick(s); targeting {DrawSlipSize} pick(s).",
            drawPassers.Count,
            settings.DrawSlipSize);

        var orderedPassers = drawPassers
            .OrderByDescending(p => p.Candidate.ResearchScore ?? (double)p.Candidate.Confidence)
            .ThenBy(p => p.Prediction.Id)
            .ToList();

        var requests = orderedPassers.Select(p => new BetslipDrawPickRequest
        {
            PredictionId = p.Prediction.Id,
            League = p.Candidate.League,
            HomeTeam = p.Candidate.HomeTeam,
            AwayTeam = p.Candidate.AwayTeam,
            Confidence = p.Candidate.Confidence,
            MatchDateTimeUtc = p.Candidate.MatchDateTimeUtc,
            PredictionCategory = p.Candidate.PredictionCategory
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

        var byId = orderedPassers.ToDictionary(p => p.Prediction.Id);
        var picks = new List<BetslipComposerCandidate>();
        foreach (var pick in selected)
        {
            if (!byId.TryGetValue(pick.PredictionId, out var passer))
            {
                continue;
            }

            picks.Add(WithAiNote(passer.Candidate, pick.Reason));
        }

        if (picks.Count == 0)
        {
            picks = orderedPassers.Take(settings.DrawSlipSize).Select(p => p.Candidate).ToList();
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

        if ((composed.IsBanker || composed.IsRollover) &&
            combinedOdds is > 1 &&
            composed.ActiveMinOdds is double featuredMin &&
            composed.ActiveMaxOdds is double featuredMax &&
            !BankerSlipComposer.IsWithinOddsRange(combinedOdds.Value, featuredMin, featuredMax))
        {
            var rangeKind = composed.IsRollover ? "rollover" : "banker";
            statusParts.Add(
                $"After booking skips, total odds {combinedOdds.Value:0.00}x fell outside the {featuredMin:0.##}-{featuredMax:0.##}x {rangeKind} range.");
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

    private async Task<List<ComposedBetslip>> ComposeLadderFromPassersAsync(
        IReadOnlyList<LiveQuotedCandidate> passers,
        IReadOnlyList<PayoutBandSpec> bands,
        BetslipSettings settings)
    {
        if (passers.Count == 0 || bands.Count == 0)
        {
            return [];
        }

        var pool = passers
            .Select(p => p.Candidate)
            .OrderByDescending(c => c.ResearchScore ?? (double)c.Confidence)
            .ThenBy(c => c.PredictionId)
            .ToList();

        var aiSlips = new List<ComposedBetslip>();
        var usedFixtures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedPredictionIds = new HashSet<int>();

        try
        {
            var requests = pool.Select(ToLadderRankRequest).ToList();
            var bandRequests = bands.Select(b => new LadderComposeBandRequest
            {
                SlipNumber = b.SlipNumber,
                Title = b.Title,
                BandKey = b.BandKey,
                MinOdds = b.MinOdds,
                MaxOdds = b.MaxOdds,
                FallbackMinOdds = b.FallbackMinOdds,
                FallbackMaxOdds = b.FallbackMaxOdds,
                MaxPicks = b.MaxPicks
            }).ToList();

            var composed = await _aiAdvisorService.ComposeLadderSlipsAsync(requests, bandRequests);
            var bySlipNumber = composed.Slips.ToDictionary(s => s.SlipNumber);
            foreach (var band in bands)
            {
                if (!bySlipNumber.TryGetValue(band.SlipNumber, out var aiSlip))
                {
                    continue;
                }

                var validated = TryValidateAiLadderSlip(aiSlip.PredictionIds, band, pool, usedFixtures, usedPredictionIds);
                if (validated is null)
                {
                    continue;
                }

                aiSlips.Add(validated);
                foreach (var selection in validated.Selections)
                {
                    usedPredictionIds.Add(selection.PredictionId);
                    if (!string.IsNullOrWhiteSpace(selection.FixtureKey))
                    {
                        usedFixtures.Add(selection.FixtureKey);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ladder AI compose failed; packing remaining bands deterministically.");
        }

        var missingBands = bands.Where(b => aiSlips.All(s => s.SlipNumber != b.SlipNumber)).ToList();
        if (missingBands.Count == 0)
        {
            return aiSlips.OrderBy(s => s.SlipNumber).ToList();
        }

        var leftover = pool
            .Where(c => !usedPredictionIds.Contains(c.PredictionId) && !IsFixtureUsed(c.FixtureKey, usedFixtures))
            .ToList();
        var packed = WeekendPayoutSlipComposer.Compose(
            leftover,
            missingBands,
            settings.MaxSlipsPerPrediction,
            settings.MaxSingleMarketShare,
            settings.OverlapPenalty);

        if (packed.Count > 0)
        {
            _logger.LogInformation(
                "Ladder packer filled {PackedCount} band(s) the AI missed: {Titles}.",
                packed.Count,
                string.Join(", ", packed.Select(s => s.Title)));
        }

        return aiSlips.Concat(packed).OrderBy(s => s.SlipNumber).ToList();
    }

    private static ComposedBetslip? TryValidateAiLadderSlip(
        IReadOnlyList<int> predictionIds,
        PayoutBandSpec band,
        IReadOnlyList<BetslipComposerCandidate> pool,
        IReadOnlySet<string> usedFixtures,
        IReadOnlySet<int> usedPredictionIds)
    {
        var byId = pool.ToDictionary(c => c.PredictionId);
        var selected = new List<BetslipComposerCandidate>();
        var slipFixtures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var predictionId in predictionIds)
        {
            if (selected.Count >= Math.Max(1, band.MaxPicks))
            {
                break;
            }

            if (usedPredictionIds.Contains(predictionId) || !byId.TryGetValue(predictionId, out var candidate))
            {
                continue;
            }

            if (candidate.DecimalOdds is not > 1d)
            {
                continue;
            }

            var fixtureKey = string.IsNullOrWhiteSpace(candidate.FixtureKey)
                ? $"{candidate.League}|{candidate.HomeTeam}|{candidate.AwayTeam}"
                : candidate.FixtureKey;
            if (IsFixtureUsed(fixtureKey, usedFixtures) || !slipFixtures.Add(fixtureKey))
            {
                continue;
            }

            selected.Add(candidate);
        }

        if (selected.Count == 0)
        {
            return null;
        }

        var combined = BankerSlipComposer.CalculateCombinedOdds(selected);
        var usedFallback = false;
        var activeMin = band.MinOdds;
        var activeMax = band.MaxOdds;
        if (!BankerSlipComposer.IsWithinOddsRange(combined, band.MinOdds, band.MaxOdds))
        {
            if (!BankerSlipComposer.IsWithinOddsRange(combined, band.FallbackMinOdds, band.FallbackMaxOdds))
            {
                return null;
            }

            usedFallback = true;
            activeMin = band.FallbackMinOdds;
            activeMax = band.FallbackMaxOdds;
        }

        return new ComposedBetslip
        {
            SlipNumber = band.SlipNumber,
            Title = band.Title,
            TierLabel = band.TierLabel,
            TargetMinSelections = 1,
            TargetMaxSelections = band.MaxPicks,
            Selections = selected,
            ShortfallNote = usedFallback
                ? $"Widened payout range to {activeMin:0.##}-{activeMax:0.##}x."
                : null,
            TargetCombinedOdds = combined,
            ActiveMinOdds = activeMin,
            ActiveMaxOdds = activeMax,
            IsPayoutBand = true,
            IsMega = band.IsMega,
            AiSummary = "AI-composed payout band from screened live-priced passers."
        };
    }

    private static LadderRankRequest ToLadderRankRequest(BetslipComposerCandidate candidate) =>
        new()
        {
            PredictionId = candidate.PredictionId,
            League = candidate.League,
            HomeTeam = candidate.HomeTeam,
            AwayTeam = candidate.AwayTeam,
            Market = candidate.Market,
            PredictedOutcome = candidate.PredictedOutcome,
            PredictionCategory = candidate.PredictionCategory,
            Confidence = candidate.Confidence,
            DecimalOdds = candidate.DecimalOdds ?? 0d,
            MatchDateTimeUtc = candidate.MatchDateTimeUtc
        };

    private static BetslipComposerCandidate WithResearchScore(
        BetslipComposerCandidate candidate,
        double researchScore) =>
        new()
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
            AiNote = candidate.AiNote,
            ResearchScore = researchScore
        };

    private static BetslipComposerCandidate WithAiNote(BetslipComposerCandidate candidate, string? reason) =>
        new()
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
            AiNote = string.IsNullOrWhiteSpace(reason) ? candidate.AiNote : reason.Trim(),
            ResearchScore = candidate.ResearchScore
        };

    private List<LiveQuotedCandidate> BuildLiveQuotedUniverse(
        IReadOnlyList<Prediction> predictions,
        IReadOnlyList<SourceMarketFixture> fixtures)
    {
        if (fixtures.Count == 0)
        {
            _logger.LogInformation("Live-quoted universe empty: no SportyBet fixtures for today.");
            return [];
        }

        var screenable = predictions
            .Where(p => MainCategories.Contains(p.PredictionCategory) || p.PredictionCategory == "Draw")
            .ToList();
        var liveQuoted = new List<LiveQuotedCandidate>();
        var unmatched = 0;
        var noLiveQuote = 0;
        var unmatchedSample = new List<string>();

        foreach (var prediction in screenable)
        {
            var fixture = SourceMarketFixtureMatcher.FindBestFixture(
                fixtures,
                prediction.HomeTeam,
                prediction.AwayTeam,
                prediction.League,
                prediction.MatchDateTime);

            if (fixture is null)
            {
                unmatched++;
                if (unmatchedSample.Count < 8)
                {
                    unmatchedSample.Add($"{prediction.HomeTeam} vs {prediction.AwayTeam} ({prediction.League})");
                }

                continue;
            }

            if (!TryGetLiveQuote(prediction, fixture, out var liveOdds, out var marketProbability))
            {
                noLiveQuote++;
                continue;
            }

            var baseCandidate = ToCandidate(prediction, new Dictionary<int, double>());
            liveQuoted.Add(new LiveQuotedCandidate
            {
                Prediction = prediction,
                Candidate = new BetslipComposerCandidate
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
                },
                MarketProbability = marketProbability
            });
        }

        _logger.LogInformation(
            "Live-quoted universe: {Published} published main/draw picks, {SportyBetFixtures} SportyBet fixtures; unmatched={Unmatched}, noLiveQuote={NoLiveQuote}, liveQuoted={LiveQuoted}.",
            screenable.Count,
            fixtures.Count,
            unmatched,
            noLiveQuote,
            liveQuoted.Count);

        if (unmatchedSample.Count > 0)
        {
            _logger.LogInformation("Live-quoted unmatched sample: {UnmatchedSample}.", string.Join("; ", unmatchedSample));
        }

        return liveQuoted;
    }

    private void LogComposeFunnel(
        IReadOnlyList<LiveQuotedCandidate> universe,
        IReadOnlyList<LiveQuotedCandidate> collapsedMain,
        int aiPassedCount)
    {
        var uniqueFixtures = universe
            .Select(c => c.Candidate.FixtureKey)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        _logger.LogInformation(
            "Betslip compose funnel: liveQuoted={LiveQuoted}, uniqueFixtures={UniqueFixtures}, main3pct={Main3}, main1pct={Main1}, aiPassed={AiPassed}, afterCollapse main3pct={Collapsed3}, main1pct={Collapsed1}.",
            universe.Count,
            uniqueFixtures,
            CountMainWithEdge(universe, _minimumEdge),
            CountMainWithEdge(universe, _ladderMinimumEdge),
            aiPassedCount,
            CountMainWithEdge(collapsedMain, _minimumEdge),
            CountMainWithEdge(collapsedMain, _ladderMinimumEdge));
    }

    private static int CountMainWithEdge(IEnumerable<LiveQuotedCandidate> candidates, double minimumEdge) =>
        candidates.Count(p =>
            MainCategories.Contains(p.Candidate.PredictionCategory) &&
            BetPricingMath.MeetsMinimumEdge(
                (double)p.Candidate.Confidence,
                p.MarketProbability,
                minimumEdge));

    private async Task<(List<LiveQuotedCandidate> Ranked, int PassedCount)> ScreenLiveQuotedUniverseAsync(
        List<LiveQuotedCandidate> universe)
    {
        if (universe.Count == 0)
        {
            return ([], 0);
        }

        var batchSize = Math.Clamp(_settings.ScreenBatchSize, 5, 50);
        var passedById = new Dictionary<int, BetslipScreenPick>();
        var fallbackIds = new HashSet<int>();
        var batchCount = 0;

        foreach (var batch in universe.Chunk(batchSize))
        {
            batchCount++;
            var requests = batch.Select(ToScreenRequest).ToList();
            try
            {
                var result = await _aiAdvisorService.ScreenBetslipCandidatesAsync(requests);
                foreach (var pick in result.Passed)
                {
                    passedById[pick.PredictionId] = pick;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Betslip screening batch {BatchNumber} failed; keeping {BatchSize} candidates as confidence-ranked packable picks.",
                    batchCount,
                    batch.Length);
                foreach (var candidate in batch)
                {
                    fallbackIds.Add(candidate.Prediction.Id);
                }
            }
        }

        var ranked = new List<LiveQuotedCandidate>(universe.Count);
        var rejectedSample = new List<string>();
        foreach (var candidate in universe)
        {
            if (passedById.TryGetValue(candidate.Prediction.Id, out var pick))
            {
                ranked.Add(candidate with
                {
                    Candidate = WithResearchScore(
                        WithAiNote(candidate.Candidate, pick.Reason),
                        pick.Score)
                });
                continue;
            }

            ranked.Add(candidate with
            {
                Candidate = WithResearchScore(candidate.Candidate, (double)candidate.Candidate.Confidence * 100d)
            });

            if (!fallbackIds.Contains(candidate.Prediction.Id) && rejectedSample.Count < 8)
            {
                rejectedSample.Add(
                    $"{candidate.Candidate.HomeTeam} vs {candidate.Candidate.AwayTeam} ({candidate.Candidate.Market})");
            }
        }

        var rejectedCount = universe.Count - passedById.Count - fallbackIds.Count;
        _logger.LogInformation(
            "Betslip screening: {BatchCount} batch(es) of {BatchSize}; liveQuoted={LiveQuoted}, passed={Passed}, fallback={Fallback}, rejected={Rejected}.",
            batchCount,
            batchSize,
            universe.Count,
            passedById.Count,
            fallbackIds.Count,
            rejectedCount);

        if (rejectedSample.Count > 0)
        {
            _logger.LogInformation("Betslip screening reject sample: {RejectedSample}.", string.Join("; ", rejectedSample));
        }

        return (ranked, passedById.Count);
    }

    private static List<LiveQuotedCandidate> CollapseOnePerFixture(IReadOnlyList<LiveQuotedCandidate> passers) =>
        passers
            .GroupBy(p => p.Candidate.FixtureKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderByDescending(p => p.Candidate.ResearchScore ?? (double)p.Candidate.Confidence)
                .ThenBy(p => p.Prediction.Id)
                .First())
            .OrderByDescending(p => p.Candidate.ResearchScore ?? (double)p.Candidate.Confidence)
            .ThenBy(p => p.Prediction.Id)
            .ToList();

    private static List<LiveQuotedCandidate> FilterByCategoryAndEdge(
        IReadOnlyList<LiveQuotedCandidate> passers,
        IReadOnlyCollection<string> categories,
        double minimumEdge) =>
        passers
            .Where(p => categories.Contains(p.Candidate.PredictionCategory))
            .Where(p => BetPricingMath.MeetsMinimumEdge(
                (double)p.Candidate.Confidence,
                p.MarketProbability,
                minimumEdge))
            .ToList();

    private static BetslipScreenRequest ToScreenRequest(LiveQuotedCandidate candidate) =>
        new()
        {
            PredictionId = candidate.Prediction.Id,
            League = candidate.Candidate.League,
            HomeTeam = candidate.Candidate.HomeTeam,
            AwayTeam = candidate.Candidate.AwayTeam,
            Market = candidate.Candidate.Market,
            PredictedOutcome = candidate.Candidate.PredictedOutcome,
            PredictionCategory = candidate.Candidate.PredictionCategory,
            Confidence = candidate.Candidate.Confidence,
            DecimalOdds = candidate.Candidate.DecimalOdds ?? 0d,
            MatchDateTimeUtc = candidate.Candidate.MatchDateTimeUtc
        };

    private sealed record LiveQuotedCandidate
    {
        public required Prediction Prediction { get; init; }
        public required BetslipComposerCandidate Candidate { get; init; }
        public required double MarketProbability { get; init; }
    }

    private bool TryGetLiveQuote(
        Prediction prediction,
        SourceMarketFixture? fixture,
        out double liveOdds,
        out double marketProbability)
    {
        liveOdds = 0d;
        marketProbability = 0d;
        if (!MarketQuoteResolver.TryResolveStakeableQuote(prediction, fixture, storedMatch: null, out var quote) ||
            quote.DecimalOdds <= 1d)
        {
            return false;
        }

        liveOdds = quote.DecimalOdds;
        marketProbability = quote.MarketProbability;
        return true;
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

    private static List<LiveQuotedCandidate> ExcludeUsedFixtures(
        IReadOnlyList<LiveQuotedCandidate> candidates,
        IReadOnlySet<string> usedFixtureKeys)
    {
        if (usedFixtureKeys.Count == 0)
        {
            return candidates.ToList();
        }

        return candidates
            .Where(c => !IsFixtureUsed(c.Candidate.FixtureKey, usedFixtureKeys))
            .ToList();
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
