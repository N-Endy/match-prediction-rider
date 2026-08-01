using Hangfire;
using MatchPredictor.Application.Helpers;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MatchPredictor.Application.Services;

public sealed class BetslipGenerationService : IBetslipGenerationService
{
    private const string JobResource = "matchpredictor-betslips";

    private static readonly string[] MainCategories =
    [
        "StraightWin",
        "BothTeamsScore",
        "Over2.5Goals",
        "Under2.5Goals"
    ];

    private readonly ApplicationDbContext _dbContext;
    private readonly ISportyBetBookingService _bookingService;
    private readonly IAiAdvisorService _aiAdvisorService;
    private readonly BetslipSettings _settings;
    private readonly ILogger<BetslipGenerationService> _logger;

    public BetslipGenerationService(
        ApplicationDbContext dbContext,
        ISportyBetBookingService bookingService,
        IAiAdvisorService aiAdvisorService,
        IOptions<BetslipSettings> options,
        ILogger<BetslipGenerationService> logger)
    {
        _dbContext = dbContext;
        _bookingService = bookingService;
        _aiAdvisorService = aiAdvisorService;
        _settings = options.Value;
        _logger = logger;
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

            var mainPool = predictions
                .Where(p => MainCategories.Contains(p.PredictionCategory))
                .Select(p => ToCandidate(p, oddsByPredictionId))
                .OrderByDescending(c => c.Confidence)
                .ThenBy(c => c.PredictionId)
                .ToList();

            var tiers = dayKind == BetslipDayKinds.Weekend
                ? BetslipComposer.WeekendTierPlan(settings.MaxSelectionsPerSlip)
                : BetslipComposer.WeekdayTierPlan(settings.MaxSelectionsPerSlip);

            var composed = BetslipComposer.Compose(
                mainPool,
                tiers,
                settings.MaxSlipsPerPrediction,
                settings.MaxSingleMarketShare,
                settings.OverlapPenalty,
                settings.OverProvisionFactor).ToList();

            if (dayKind == BetslipDayKinds.Weekend)
            {
                var drawSlip = await ComposeDrawSlipAsync(predictions, oddsByPredictionId, settings);
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

    private async Task<ComposedBetslip?> ComposeDrawSlipAsync(
        IReadOnlyList<Prediction> predictions,
        IReadOnlyDictionary<int, double> oddsByPredictionId,
        BetslipSettings settings)
    {
        var drawCandidates = predictions
            .Where(p => p.PredictionCategory == "Draw")
            .OrderByDescending(p => p.ConfidenceScore ?? p.RawConfidenceScore ?? 0m)
            .ThenBy(p => p.Id)
            .Take(Math.Max(settings.DrawCandidatePoolSize, settings.DrawSlipSize))
            .ToList();

        if (drawCandidates.Count == 0)
        {
            return null;
        }

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
                : null
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
                // Share code exists; without per-pick unresolved IDs, show selections as included.
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

        double? combinedOdds = null;
        var oddsValues = selections.Where(s => s.WasBooked && s.DecimalOdds is > 1).Select(s => s.DecimalOdds!.Value).ToList();
        if (oddsValues.Count > 0 && oddsValues.Count == bookedCount)
        {
            combinedOdds = oddsValues.Aggregate(1d, (acc, odds) => acc * odds);
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
            AiSummary = string.Equals(composed.TierLabel, "AI Draws (5)", StringComparison.Ordinal)
                ? "AI-selected best draw games for today."
                : null,
            Selections = selections
        };
    }

    private static BetslipComposerCandidate ToCandidate(
        Prediction prediction,
        IReadOnlyDictionary<int, double> oddsByPredictionId)
    {
        oddsByPredictionId.TryGetValue(prediction.Id, out var odds);
        return new BetslipComposerCandidate
        {
            PredictionId = prediction.Id,
            FixtureKey = string.IsNullOrWhiteSpace(prediction.FixtureKey)
                ? $"{prediction.League}|{prediction.HomeTeam}|{prediction.AwayTeam}|{prediction.MatchLocalDate}"
                : prediction.FixtureKey,
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
}
