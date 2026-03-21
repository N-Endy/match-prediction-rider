using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace MatchPredictor.Infrastructure.Services;

/// <summary>
/// SportyBet booking service using the internal HTTP API (no Selenium/Chrome needed).
/// 
/// Flow:
///   1. Fetch today's fixtures from SportyBet to map team names → SportyBet event/outcome IDs
///   2. POST the collected outcome IDs to /api/ng/orders/share → get booking code
/// 
/// This is ~100x faster than Selenium and uses no additional RAM.
/// No login required for booking code generation.
/// </summary>
public class SportyBetBookingService : ISportyBetBookingService, ISourceMarketPricingService
{
    private const string PricingClientName = "SportyBetPricing";
    private const string BookingClientName = "SportyBetBooking";
    private const int DefaultPricingPageSize = 100;
    private const int DefaultBookingPageSize = 100;
    private const int DefaultPricingMaxPages = 10;
    private const int DefaultBookingMaxPages = 10;
    private const double MinimumDirectionalTeamScore = 0.72;
    private const double MinimumConfidentMatchScore = 1.55;
    private const double AmbiguousScoreGap = 0.12;
    private static readonly TimeSpan TightKickoffWindow = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan LooseKickoffWindow = TimeSpan.FromMinutes(90);
    private static readonly TimeSpan MaximumKickoffWindow = TimeSpan.FromHours(6);
    private static readonly TimeSpan FullFixtureCacheTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan BackupFixtureCacheTtl = TimeSpan.FromHours(6);
    private static readonly Regex NonWordRegex = new("[^a-z0-9]+", RegexOptions.Compiled);
    private static readonly HashSet<string> TeamNoiseWords =
    [
        "fc", "cf", "sc", "afc", "club", "the", "de", "da", "do", "cd", "ud", "ac", "as", "fk", "sk", "nk", "if", "bk"
    ];
    private static readonly HashSet<string> LeagueNoiseWords =
    [
        "league", "division", "group", "round", "stage", "play", "offs"
    ];
    private static readonly Dictionary<string, string> TokenSynonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["utd"] = "united",
        ["st"] = "saint",
        ["ii"] = "reserve",
        ["iii"] = "reserve3",
        ["b"] = "reserve",
        ["res"] = "reserve",
        ["reserves"] = "reserve",
        ["ladies"] = "women",
        ["fem"] = "women"
    };

    private readonly IConfiguration _configuration;
    private readonly ILogger<SportyBetBookingService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDistributedCache _cache;
    private readonly ApplicationDbContext _dbContext;

    // Values read from appsettings SportyBet section

    public SportyBetBookingService(
        IConfiguration configuration,
        ILogger<SportyBetBookingService> logger,
        IHttpClientFactory httpClientFactory,
        IDistributedCache cache,
        ApplicationDbContext dbContext)
    {
        _configuration = configuration;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _dbContext = dbContext;
    }

    public async Task<BookingResult> BookGamesAsync(List<BookingSelection> selections)
    {
        if (selections.Count == 0)
            return new BookingResult { Success = false, Message = "No games selected." };

        var baseUrl = _configuration["SportyBet:BaseUrl"] ?? "https://www.sportybet.com";
        var soccerSportId = _configuration["SportyBet:SoccerSportId"] ?? "sr:sport:1";
        var market1X2 = _configuration["SportyBet:Market1X2"] ?? "1";
        var todayLocalDate = DateTimeProvider.GetLocalDate();

        try
        {
            _logger.LogInformation("Searching SportyBet API for {Count} selections...", selections.Count);
            var canonicalSelections = await BuildCanonicalSelectionsAsync(selections, CancellationToken.None);
            var matchableSelections = canonicalSelections
                .Where(selection => !selection.MatchLocalDate.HasValue || selection.MatchLocalDate.Value == todayLocalDate)
                .ToList();
            var warnings = canonicalSelections
                .Where(selection => selection.MatchLocalDate.HasValue && selection.MatchLocalDate.Value != todayLocalDate)
                .Select(selection => BuildSelectionWarning(selection, BookingSelectionMatchStatus.OutsideTodayWindow))
                .ToList();

            foreach (var skippedSelection in canonicalSelections.Except(matchableSelections))
            {
                _logger.LogInformation(
                    "Skipping SportyBet booking for {SelectionLabel} because it falls outside today's SportyBet card.",
                    skippedSelection.SelectionLabel);
            }

            if (matchableSelections.Count == 0)
            {
                return BuildBookingFailureResult(
                    "All selected matches fall outside today's SportyBet card.",
                    bookedCount: 0,
                    totalSelections: selections.Count,
                    warnings);
            }

            // Step 1: Fetch today's fixtures from SportyBet to get outcome IDs
            var fixtureMap = await FetchTodayFixturesAsync(
                baseUrl,
                soccerSportId,
                market1X2,
                CancellationToken.None,
                useBookingClient: true,
                targetedSelections: matchableSelections);
            if (fixtureMap.Count == 0)
            {
                _logger.LogWarning("Could not fetch fixtures from SportyBet API.");
                return BuildBookingFailureResult(
                    "Could not fetch today's fixtures from SportyBet.",
                    bookedCount: 0,
                    totalSelections: selections.Count,
                    warnings);
            }

            _logger.LogInformation("Fetched {Count} fixtures from SportyBet.", fixtureMap.Count);

            // Step 2: Match each selection to a SportyBet fixture and get the right outcome
            var selectedOutcomes = new List<SportyBetOutcome>();

            foreach (var selection in matchableSelections)
            {
                var resolution = ResolveSelection(fixtureMap, selection);
                if (resolution.Outcome is not null)
                {
                    selectedOutcomes.Add(resolution.Outcome);
                    _logger.LogInformation("Matched: {Home} vs {Away} → outcomeId={OutcomeId}",
                        selection.HomeTeam, selection.AwayTeam, resolution.Outcome.OutcomeId);
                }
                else
                {
                    var warning = BuildSelectionWarning(selection, resolution.Status, resolution.MatchedFixture);
                    warnings.Add(warning);
                    _logger.LogWarning(
                        "SportyBet booking skipped for {SelectionLabel}. Reason: {Reason}",
                        selection.SelectionLabel,
                        resolution.Status);
                }
            }

            if (selectedOutcomes.Count == 0)
            {
                return BuildBookingFailureResult(
                    "None of the selected matches could be booked on SportyBet today.",
                    bookedCount: 0,
                    totalSelections: selections.Count,
                    warnings);
            }

            // Step 3: Create booking code via API
            var (bookingCode, bookingUrl) = await CreateBookingCodeAsync(selectedOutcomes, baseUrl);
            var skippedCount = selections.Count - selectedOutcomes.Count;

            if (!string.IsNullOrEmpty(bookingCode))
            {
                return new BookingResult
                {
                    Success = true,
                    BookingCode = bookingCode,
                    BookingUrl = bookingUrl ?? "",
                    Message = skippedCount > 0
                        ? $"Booked {selectedOutcomes.Count}/{selections.Count} games. The skipped picks are listed below."
                        : $"Booked {selectedOutcomes.Count}/{selections.Count} games.",
                    BookedCount = selectedOutcomes.Count,
                    SkippedCount = skippedCount,
                    Warnings = warnings
                };
            }

            return BuildBookingFailureResult(
                $"Found {selectedOutcomes.Count} matches but could not generate a SportyBet booking code.",
                bookedCount: selectedOutcomes.Count,
                totalSelections: selections.Count,
                warnings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during SportyBet booking via API.");
            return new BookingResult
            {
                Success = false,
                Message = $"Booking error: {ex.Message}",
                BookedCount = 0,
                SkippedCount = selections.Count
            };
        }
    }

    public async Task<IReadOnlyList<SourceMarketFixture>> GetTodaySourceMarketFixturesAsync(CancellationToken ct = default)
    {
        var baseUrl = _configuration["SportyBet:BaseUrl"] ?? "https://www.sportybet.com";
        var soccerSportId = _configuration["SportyBet:SoccerSportId"] ?? "sr:sport:1";
        var market1X2 = _configuration["SportyBet:Market1X2"] ?? "1";

        var fixtures = await FetchTodayFixturesAsync(baseUrl, soccerSportId, market1X2, ct, useBookingClient: false);
        return fixtures.Select(fixture => new SourceMarketFixture
        {
            EventId = fixture.EventId,
            League = fixture.League,
            HomeTeam = fixture.HomeTeam,
            AwayTeam = fixture.AwayTeam,
            MatchTimeUtc = fixture.MatchTimeUtc,
            HomeWinProbability = fixture.HomeProbability,
            HomeWinOdds = fixture.HomeOdds,
            DrawProbability = fixture.DrawProbability,
            DrawOdds = fixture.DrawOdds,
            AwayWinProbability = fixture.AwayProbability,
            AwayWinOdds = fixture.AwayOdds,
            Over25Probability = fixture.Over25Probability,
            Over25Odds = fixture.Over25Odds,
            Under25Probability = fixture.Under25Probability,
            Under25Odds = fixture.Under25Odds,
            BttsYesProbability = fixture.BttsYesProbability,
            BttsYesOdds = fixture.BttsYesOdds,
            BttsNoProbability = fixture.BttsNoProbability,
            BttsNoOdds = fixture.BttsNoOdds
        }).ToList();
    }

    /// <summary>
    /// Fetches today's football fixtures from SportyBet and returns a flat list indexed by fixture.
    /// </summary>
    private async Task<List<SportyBetFixture>> FetchTodayFixturesAsync(
        string baseUrl,
        string soccerSportId,
        string market1X2,
        CancellationToken ct,
        bool useBookingClient,
        IReadOnlyCollection<ResolvedBookingSelection>? targetedSelections = null)
    {
        var cacheKey = $"sportybet_fixtures_{DateTime.UtcNow:yyyyMMdd}";
        var backupCacheKey = $"{cacheKey}_backup";
        string? cachedData = null;

        try
        {
            cachedData = await _cache.GetStringAsync(cacheKey, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read from Redis cache. Proceeding to fetch from API.");
        }

        if (!string.IsNullOrEmpty(cachedData))
        {
            var cachedFixtures = JsonSerializer.Deserialize<List<SportyBetFixture>>(cachedData) ?? new List<SportyBetFixture>();
            var deduplicatedCachedFixtures = DeduplicateFixturesByEventId(cachedFixtures);
            if (targetedSelections is null || CanResolveSelections(deduplicatedCachedFixtures, targetedSelections))
            {
                _logger.LogInformation("Returning SportyBet fixtures from Redis cache.");
                return deduplicatedCachedFixtures;
            }

            _logger.LogInformation("Redis cache did not cover all targeted SportyBet booking selections. Continuing with live fetch.");
        }

        var fixturesByEventId = new Dictionary<string, SportyBetFixture>(StringComparer.Ordinal);
        var client = CreateHttpClient(useBookingClient ? BookingClientName : PricingClientName);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var pageSize = ResolvePageSize(useBookingClient);
        var maxPages = ResolveMaxPages(useBookingClient);

        // Paginate — SportyBet uses pageNum (not pageIndex), todayGames=true, timeline=2.9
        for (int page = 1; page <= maxPages; page++)
        {
            try
            {
                // Request multiple markets: 1 (1X2), 18 (Over/Under), 29 (GG/NG / BTTS)
                var url = $"{baseUrl}/api/ng/factsCenter/pcUpcomingEvents" +
                           $"?sportId={Uri.EscapeDataString(soccerSportId)}" +
                           $"&marketId={Uri.EscapeDataString(market1X2)},18,29" +
                           $"&pageSize={pageSize}&pageNum={page}" +
                           $"&todayGames=true&timeline=2.9&_t={timestamp}";

                _logger.LogInformation("SportyBet API GET: {Url}", url);

                var response = await client.GetAsync(url, ct);
                var responseBody = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("SportyBet page {Page} returned {Status}. Body: {Body}",
                        page, response.StatusCode, responseBody[..Math.Min(500, responseBody.Length)]);
                    break;
                }

                _logger.LogInformation("SportyBet response: {Length} chars", responseBody.Length);

                var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;

                if (!root.TryGetProperty("data", out var data))
                {
                    _logger.LogWarning("SportyBet response has no 'data' property.");
                    break;
                }

                // SportyBet nests events inside data.tournaments[].events[]
                if (!data.TryGetProperty("tournaments", out var tournaments))
                {
                    _logger.LogWarning("SportyBet 'data' has no 'tournaments' property.");
                    break;
                }

                var tournamentCount = 0;
                foreach (var tournament in tournaments.EnumerateArray())
                {
                    tournamentCount++;
                    if (!tournament.TryGetProperty("events", out var events)) continue;

                    foreach (var ev in events.EnumerateArray())
                    {
                        try
                        {
                            var homeTeam = ev.GetProperty("homeTeamName").GetString() ?? "";
                            var awayTeam = ev.GetProperty("awayTeamName").GetString() ?? "";
                            var eventId = ev.GetProperty("eventId").GetString() ?? "";
                            var kickoffTimeUtc = ev.TryGetProperty("estimateStartTime", out var estimateStartTimeElement) &&
                                                 estimateStartTimeElement.TryGetInt64(out var estimateStartTime)
                                ? DateTimeOffset.FromUnixTimeMilliseconds(estimateStartTime).UtcDateTime
                                : (DateTime?)null;
                            var league = ExtractLeagueName(ev);

                            // Extract 1X2 market outcomes
                            var homeOutcomeId = "";
                            var drawOutcomeId = "";
                            var awayOutcomeId = "";
                            var bttsYesOutcomeId = "";
                            var bttsNoOutcomeId = "";
                            var over25OutcomeId = "";
                            var under25OutcomeId = "";
                            double? homeProbability = null;
                            double? drawProbability = null;
                            double? awayProbability = null;
                            double? bttsYesProbability = null;
                            double? bttsNoProbability = null;
                            double? over25Probability = null;
                            double? under25Probability = null;
                            double? homeOdds = null;
                            double? drawOdds = null;
                            double? awayOdds = null;
                            double? bttsYesOdds = null;
                            double? bttsNoOdds = null;
                            double? over25Odds = null;
                            double? under25Odds = null;

                            if (ev.TryGetProperty("markets", out var markets))
                            {
                                foreach (var market in markets.EnumerateArray())
                                {
                                    var marketId = market.GetProperty("id").GetString() ?? "";

                                    if (marketId == market1X2)
                                    {
                                        // 1X2 market: outcomes are Home(1), Draw(X/2), Away(2/3)
                                        if (market.TryGetProperty("outcomes", out var outcomes))
                                        {
                                            foreach (var o in outcomes.EnumerateArray())
                                                {
                                                    var oid = o.GetProperty("id").GetString() ?? "";
                                                    var desc = o.TryGetProperty("desc", out var d) ? d.GetString() ?? "" : "";
                                                    var probability = TryParseProbability(o);
                                                    var decimalOdds = TryParseDecimalOdds(o);
                                                
                                                if (oid == "1" || desc.Contains("Home", StringComparison.OrdinalIgnoreCase))
                                                {
                                                    homeOutcomeId = oid;
                                                    homeProbability = probability;
                                                    homeOdds = decimalOdds;
                                                }
                                                else if (oid == "2" || desc.Contains("Draw", StringComparison.OrdinalIgnoreCase))
                                                {
                                                    drawOutcomeId = oid;
                                                    drawProbability = probability;
                                                    drawOdds = decimalOdds;
                                                }
                                                else if (oid == "3" || desc.Contains("Away", StringComparison.OrdinalIgnoreCase))
                                                {
                                                    awayOutcomeId = oid;
                                                    awayProbability = probability;
                                                    awayOdds = decimalOdds;
                                                }
                                            }
                                        }
                                    }
                                    else if (marketId == "18") // Over/Under
                                    {
                                        var specifier = market.TryGetProperty("specifier", out var spec) ? spec.GetString() : "";
                                        if (specifier == "total=2.5")
                                        {
                                            if (market.TryGetProperty("outcomes", out var outcomes))
                                            {
                                                foreach (var o in outcomes.EnumerateArray())
                                                {
                                                    var oid = o.GetProperty("id").GetString() ?? "";
                                                    var desc = o.TryGetProperty("desc", out var d) ? d.GetString() ?? "" : "";
                                                    var probability = TryParseProbability(o);
                                                    var decimalOdds = TryParseDecimalOdds(o);
                                                    if (oid == "12" || desc.Contains("Over", StringComparison.OrdinalIgnoreCase))
                                                    {
                                                        over25OutcomeId = oid;
                                                        over25Probability = probability;
                                                        over25Odds = decimalOdds;
                                                    }
                                                    else if (oid == "13" || desc.Contains("Under", StringComparison.OrdinalIgnoreCase))
                                                    {
                                                        under25OutcomeId = oid;
                                                        under25Probability = probability;
                                                        under25Odds = decimalOdds;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    else if (marketId == "29") // GG/NG (Both Teams to Score)
                                    {
                                        if (market.TryGetProperty("outcomes", out var outcomes))
                                        {
                                            foreach (var o in outcomes.EnumerateArray())
                                            {
                                                var oid = o.GetProperty("id").GetString() ?? "";
                                                var desc = o.TryGetProperty("desc", out var d) ? d.GetString() ?? "" : "";
                                                var probability = TryParseProbability(o);
                                                var decimalOdds = TryParseDecimalOdds(o);
                                                if (oid == "74" || desc.Equals("Yes", StringComparison.OrdinalIgnoreCase))
                                                {
                                                    bttsYesOutcomeId = oid;
                                                    bttsYesProbability = probability;
                                                    bttsYesOdds = decimalOdds;
                                                }
                                                else if (oid == "76" || desc.Equals("No", StringComparison.OrdinalIgnoreCase))
                                                {
                                                    bttsNoOutcomeId = oid;
                                                    bttsNoProbability = probability;
                                                    bttsNoOdds = decimalOdds;
                                                }
                                            }
                                        }
                                    }
                                }
                            }

                            fixturesByEventId[eventId] = new SportyBetFixture
                            {
                                EventId = eventId,
                                League = league,
                                HomeTeam = homeTeam,
                                AwayTeam = awayTeam,
                                MatchTimeUtc = kickoffTimeUtc,
                                HomeOutcomeId = homeOutcomeId,
                                DrawOutcomeId = drawOutcomeId,
                                AwayOutcomeId = awayOutcomeId,
                                BttsYesOutcomeId = bttsYesOutcomeId,
                                BttsNoOutcomeId = bttsNoOutcomeId,
                                Over25OutcomeId = over25OutcomeId,
                                Under25OutcomeId = under25OutcomeId,
                                HomeProbability = homeProbability,
                                HomeOdds = homeOdds,
                                DrawProbability = drawProbability,
                                DrawOdds = drawOdds,
                                AwayProbability = awayProbability,
                                AwayOdds = awayOdds,
                                Over25Probability = over25Probability,
                                Over25Odds = over25Odds,
                                Under25Probability = under25Probability,
                                Under25Odds = under25Odds,
                                BttsYesProbability = bttsYesProbability,
                                BttsYesOdds = bttsYesOdds,
                                BttsNoProbability = bttsNoProbability,
                                BttsNoOdds = bttsNoOdds
                            };
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Skipping fixture due to parse error");
                        }
                    }
                }

                var fixtures = fixturesByEventId.Values.ToList();
                _logger.LogInformation("SportyBet page {Page}: {Tournaments} tournaments, {Fixtures} fixtures parsed.",
                    page, tournamentCount, fixtures.Count);

                if (targetedSelections is { Count: > 0 } && CanResolveSelections(fixtures, targetedSelections))
                {
                    _logger.LogInformation(
                        "Resolved all {SelectionCount} booking selections from SportyBet after page {Page}.",
                        targetedSelections.Count,
                        page);
                    break;
                }

                // If no tournaments, we're done
                if (tournamentCount == 0) break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error fetching SportyBet fixtures page {Page}", page);
                break;
            }
        }

        var deduplicatedFixtures = fixturesByEventId.Values.ToList();
        if (deduplicatedFixtures.Count > 0)
        {
            try
            {
                if (!useBookingClient || targetedSelections is null)
                {
                    var serialized = JsonSerializer.Serialize(deduplicatedFixtures);
                    await _cache.SetStringAsync(
                        cacheKey,
                        serialized,
                        new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = FullFixtureCacheTtl },
                        ct);
                    await _cache.SetStringAsync(
                        backupCacheKey,
                        serialized,
                        new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = BackupFixtureCacheTtl },
                        ct);
                    _logger.LogInformation("Cached {Count} SportyBet fixtures in Redis.", deduplicatedFixtures.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write to Redis cache. Continuing without caching.");
            }
        }
        else if (useBookingClient)
        {
            try
            {
                var backupCachedData = await _cache.GetStringAsync(backupCacheKey, ct);
                if (!string.IsNullOrWhiteSpace(backupCachedData))
                {
                    _logger.LogWarning("Using stale SportyBet fixture backup cache for booking after live fetch returned no fixtures.");
                    var backupFixtures = JsonSerializer.Deserialize<List<SportyBetFixture>>(backupCachedData) ?? new List<SportyBetFixture>();
                    return DeduplicateFixturesByEventId(backupFixtures);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read stale SportyBet fixture backup cache.");
            }
        }

        return deduplicatedFixtures;
    }

    private async Task<List<ResolvedBookingSelection>> BuildCanonicalSelectionsAsync(
        IReadOnlyCollection<BookingSelection> selections,
        CancellationToken ct)
    {
        var predictionIds = selections
            .Where(selection => selection.PredictionId.HasValue && selection.PredictionId.Value > 0)
            .Select(selection => selection.PredictionId!.Value)
            .Distinct()
            .ToList();

        var predictionsById = predictionIds.Count == 0
            ? new Dictionary<int, Prediction>()
            : await _dbContext.Predictions
                .AsNoTracking()
                .Where(prediction => predictionIds.Contains(prediction.Id))
                .ToDictionaryAsync(prediction => prediction.Id, ct);

        return selections.Select(selection =>
        {
            predictionsById.TryGetValue(selection.PredictionId ?? 0, out var linkedPrediction);
            var homeTeam = linkedPrediction?.HomeTeam ?? selection.HomeTeam;
            var awayTeam = linkedPrediction?.AwayTeam ?? selection.AwayTeam;
            var league = linkedPrediction?.League ?? selection.League;
            var predictionText = linkedPrediction?.PredictedOutcome ?? selection.Prediction;
            var market = linkedPrediction is not null ? ToCartMarket(linkedPrediction) : selection.Market;
            var kickoffUtc = linkedPrediction?.MatchDateTime ?? selection.MatchDateTimeUtc;
            var localDate = linkedPrediction is not null
                ? linkedPrediction.MatchLocalDate != default
                    ? linkedPrediction.MatchLocalDate
                    : kickoffUtc.HasValue
                        ? DateTimeProvider.ConvertUtcToLocalDate(kickoffUtc.Value)
                        : (DateOnly?)null
                : kickoffUtc.HasValue
                    ? DateTimeProvider.ConvertUtcToLocalDate(kickoffUtc.Value)
                    : (DateOnly?)null;
            var requestedOutcome = ResolveRequestedOutcome(market, predictionText);

            return new ResolvedBookingSelection(
                selection,
                linkedPrediction?.Id,
                homeTeam,
                awayTeam,
                league,
                market,
                predictionText,
                kickoffUtc,
                localDate,
                requestedOutcome);
        }).ToList();
    }

    private BookingSelectionResolution ResolveSelection(
        IReadOnlyCollection<SportyBetFixture> fixtures,
        ResolvedBookingSelection selection)
    {
        return ResolveSelectionCore(fixtures, selection);
    }

    private static FixtureMatchCandidate EvaluateFixtureCandidate(
        SportyBetFixture fixture,
        ResolvedBookingSelection selection)
    {
        var homeScore = ComputeTeamMatchScore(selection.HomeTeam, fixture.HomeTeam);
        var awayScore = ComputeTeamMatchScore(selection.AwayTeam, fixture.AwayTeam);
        if (homeScore < MinimumDirectionalTeamScore || awayScore < MinimumDirectionalTeamScore)
        {
            return FixtureMatchCandidate.NotCandidate(fixture);
        }

        var forwardScore = homeScore + awayScore;
        var reverseHomeScore = ComputeTeamMatchScore(selection.HomeTeam, fixture.AwayTeam);
        var reverseAwayScore = ComputeTeamMatchScore(selection.AwayTeam, fixture.HomeTeam);
        var reverseLooksValid = reverseHomeScore >= MinimumDirectionalTeamScore && reverseAwayScore >= MinimumDirectionalTeamScore;
        if (reverseLooksValid && (reverseHomeScore + reverseAwayScore) >= forwardScore - 0.04d)
        {
            return FixtureMatchCandidate.NotCandidate(fixture);
        }

        TimeSpan? kickoffDelta = null;
        var score = forwardScore;

        if (selection.MatchDateTimeUtc.HasValue)
        {
            if (!fixture.MatchTimeUtc.HasValue)
            {
                return FixtureMatchCandidate.NotCandidate(fixture);
            }

            var selectionLocalDate = DateTimeProvider.ConvertUtcToLocalDate(selection.MatchDateTimeUtc.Value);
            var fixtureLocalDate = DateTimeProvider.ConvertUtcToLocalDate(fixture.MatchTimeUtc.Value);
            if (fixtureLocalDate != selectionLocalDate)
            {
                return FixtureMatchCandidate.NotCandidate(fixture);
            }

            kickoffDelta = (fixture.MatchTimeUtc.Value - selection.MatchDateTimeUtc.Value).Duration();
            if (kickoffDelta > MaximumKickoffWindow)
            {
                return FixtureMatchCandidate.NotCandidate(fixture);
            }

            score += kickoffDelta <= TightKickoffWindow
                ? 0.35d
                : kickoffDelta <= LooseKickoffWindow
                    ? 0.2d
                    : 0.08d;
        }

        if (!string.IsNullOrWhiteSpace(selection.League) && !string.IsNullOrWhiteSpace(fixture.League))
        {
            score += ComputeLeagueMatchScore(selection.League, fixture.League) * 0.18d;
        }

        return new FixtureMatchCandidate(fixture, score, kickoffDelta, true);
    }

    /// <summary>
    /// Calls POST /api/ng/orders/share to generate a booking code.
    /// Payload format reverse-engineered from SportyBet network traffic.
    /// </summary>
    private async Task<(string? Code, string? Url)> CreateBookingCodeAsync(List<SportyBetOutcome> outcomes, string baseUrl)
    {
        var client = CreateHttpClient(BookingClientName);

        // Build payload exactly as SportyBet expects — each selection needs eventId, marketId, specifier, outcomeId
        var selections = outcomes.Select(o => new
        {
            eventId = o.EventId,
            marketId = o.MarketId,
            specifier = o.Specifier,
            outcomeId = o.OutcomeId
        }).ToArray();

        var payload = new { selections };

        var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        _logger.LogInformation("SportyBet booking POST with {Count} selections: {Payload}",
            selections.Length, JsonSerializer.Serialize(payload));

        var response = await client.PostAsync($"{baseUrl}/api/ng/orders/share", body);
        var responseBody = await response.Content.ReadAsStringAsync();

        _logger.LogInformation("BookCode response: {Status} {Body}", response.StatusCode,
            responseBody[..Math.Min(500, responseBody.Length)]);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("BookCode API returned {Status}", response.StatusCode);
            return (null, null);
        }

        var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        // SportyBet returns { bizCode: 10000, data: { shareCode: "ABCDE", shareURL: "..." } }
        if (root.TryGetProperty("data", out var data))
        {
            string? code = null;
            string? url = null;

            if (data.TryGetProperty("shareCode", out var share)) code = share.GetString();
            else if (data.TryGetProperty("bookCode", out var bc)) code = bc.GetString();
            else if (data.TryGetProperty("code", out var c)) code = c.GetString();
            else if (data.ValueKind == JsonValueKind.String) code = data.GetString();

            if (data.TryGetProperty("shareURL", out var su)) url = su.GetString();

            return (code, url);
        }

        return (null, null);
    }

    private HttpClient CreateHttpClient(string clientName)
    {
        var client = _httpClientFactory.CreateClient(clientName);
        var baseUrl = _configuration["SportyBet:BaseUrl"] ?? "https://www.sportybet.com";
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", $"{baseUrl}/ng/");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", baseUrl);
        client.DefaultRequestHeaders.TryAddWithoutValidation("clientid", "web");
        client.DefaultRequestHeaders.TryAddWithoutValidation("platform", "web");
        client.DefaultRequestHeaders.TryAddWithoutValidation("operid", "2");
        client.Timeout = ResolveClientTimeout(clientName);
        return client;
    }

    private TimeSpan ResolveClientTimeout(string clientName)
    {
        var configKey = string.Equals(clientName, BookingClientName, StringComparison.Ordinal)
            ? "SportyBet:BookingTimeoutSeconds"
            : "SportyBet:PricingTimeoutSeconds";
        var defaultSeconds = string.Equals(clientName, BookingClientName, StringComparison.Ordinal) ? 60 : 30;
        var configuredSeconds = _configuration.GetValue<int?>(configKey);
        return TimeSpan.FromSeconds(Math.Max(10, configuredSeconds ?? defaultSeconds));
    }

    private int ResolvePageSize(bool useBookingClient)
    {
        var configKey = useBookingClient ? "SportyBet:BookingPageSize" : "SportyBet:PricingPageSize";
        var defaultSize = useBookingClient ? DefaultBookingPageSize : DefaultPricingPageSize;
        return Math.Clamp(_configuration.GetValue<int?>(configKey) ?? defaultSize, 10, 100);
    }

    private int ResolveMaxPages(bool useBookingClient)
    {
        var configKey = useBookingClient ? "SportyBet:BookingMaxPages" : "SportyBet:PricingMaxPages";
        var defaultPages = useBookingClient ? DefaultBookingMaxPages : DefaultPricingMaxPages;
        return Math.Clamp(_configuration.GetValue<int?>(configKey) ?? defaultPages, 1, 20);
    }

    private static bool CanResolveSelections(
        IReadOnlyCollection<SportyBetFixture> fixtures,
        IReadOnlyCollection<ResolvedBookingSelection> selections)
    {
        var fixtureList = fixtures.ToList();
        return selections.All(selection => ResolveSelectionCore(fixtureList, selection).Status == BookingSelectionMatchStatus.Matched);
    }

    private static BookingSelectionResolution ResolveSelectionCore(
        IReadOnlyCollection<SportyBetFixture> fixtures,
        ResolvedBookingSelection selection)
    {
        if (selection.RequestedOutcome is null)
        {
            return new BookingSelectionResolution(BookingSelectionMatchStatus.MarketUnavailable, null, null);
        }

        var evaluatedCandidates = fixtures
            .Select(fixture => EvaluateFixtureCandidate(fixture, selection))
            .Where(candidate => candidate.IsCandidate)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.KickoffDelta ?? TimeSpan.MaxValue)
            .ToList();

        if (evaluatedCandidates.Count == 0)
        {
            return new BookingSelectionResolution(BookingSelectionMatchStatus.NoFixtureFound, null, null);
        }

        var bestCandidate = evaluatedCandidates[0];
        var runnerUp = evaluatedCandidates.Count > 1 ? evaluatedCandidates[1] : null;
        var isWeak = bestCandidate.Score < MinimumConfidentMatchScore;
        var isTooClose = runnerUp is not null && bestCandidate.Score - runnerUp.Score < AmbiguousScoreGap;
        if (isWeak || isTooClose)
        {
            return new BookingSelectionResolution(BookingSelectionMatchStatus.AmbiguousFixture, null, bestCandidate.Fixture);
        }

        return TryCreateOutcome(bestCandidate.Fixture, selection.RequestedOutcome.Value, out var outcome)
            ? new BookingSelectionResolution(BookingSelectionMatchStatus.Matched, outcome, bestCandidate.Fixture)
            : new BookingSelectionResolution(BookingSelectionMatchStatus.MarketUnavailable, null, bestCandidate.Fixture);
    }

    private static BookingResult BuildBookingFailureResult(
        string message,
        int bookedCount,
        int totalSelections,
        List<string> warnings)
    {
        return new BookingResult
        {
            Success = false,
            Message = message,
            BookedCount = bookedCount,
            SkippedCount = Math.Max(0, totalSelections - bookedCount),
            Warnings = warnings
        };
    }

    private static string BuildSelectionWarning(
        ResolvedBookingSelection selection,
        BookingSelectionMatchStatus status,
        SportyBetFixture? matchedFixture = null)
    {
        var label = selection.SelectionLabel;
        return status switch
        {
            BookingSelectionMatchStatus.OutsideTodayWindow => $"{label}: outside today's SportyBet card.",
            BookingSelectionMatchStatus.NoFixtureFound => $"{label}: no SportyBet fixture found for today's card.",
            BookingSelectionMatchStatus.AmbiguousFixture => $"{label}: fixture match was ambiguous, so it was skipped.",
            BookingSelectionMatchStatus.MarketUnavailable => matchedFixture is not null
                ? $"{label}: SportyBet found {matchedFixture.HomeTeam} vs {matchedFixture.AwayTeam}, but the requested market was unavailable."
                : $"{label}: requested market unavailable on SportyBet.",
            _ => $"{label}: skipped."
        };
    }

    private static bool TryCreateOutcome(
        SportyBetFixture fixture,
        RequestedSportyBetOutcome requestedOutcome,
        out SportyBetOutcome outcome)
    {
        outcome = new SportyBetOutcome
        {
            EventId = fixture.EventId,
            HomeTeam = fixture.HomeTeam,
            AwayTeam = fixture.AwayTeam
        };

        switch (requestedOutcome)
        {
            case RequestedSportyBetOutcome.HomeWin when !string.IsNullOrWhiteSpace(fixture.HomeOutcomeId):
                outcome = outcome with { OutcomeId = fixture.HomeOutcomeId, MarketId = "1" };
                return true;
            case RequestedSportyBetOutcome.Draw when !string.IsNullOrWhiteSpace(fixture.DrawOutcomeId):
                outcome = outcome with { OutcomeId = fixture.DrawOutcomeId, MarketId = "1" };
                return true;
            case RequestedSportyBetOutcome.AwayWin when !string.IsNullOrWhiteSpace(fixture.AwayOutcomeId):
                outcome = outcome with { OutcomeId = fixture.AwayOutcomeId, MarketId = "1" };
                return true;
            case RequestedSportyBetOutcome.BttsYes when !string.IsNullOrWhiteSpace(fixture.BttsYesOutcomeId):
                outcome = outcome with { OutcomeId = fixture.BttsYesOutcomeId, MarketId = "29" };
                return true;
            case RequestedSportyBetOutcome.BttsNo when !string.IsNullOrWhiteSpace(fixture.BttsNoOutcomeId):
                outcome = outcome with { OutcomeId = fixture.BttsNoOutcomeId, MarketId = "29" };
                return true;
            case RequestedSportyBetOutcome.Over25 when !string.IsNullOrWhiteSpace(fixture.Over25OutcomeId):
                outcome = outcome with { OutcomeId = fixture.Over25OutcomeId, MarketId = "18", Specifier = "total=2.5" };
                return true;
            case RequestedSportyBetOutcome.Under25 when !string.IsNullOrWhiteSpace(fixture.Under25OutcomeId):
                outcome = outcome with { OutcomeId = fixture.Under25OutcomeId, MarketId = "18", Specifier = "total=2.5" };
                return true;
            default:
                return false;
        }
    }

    private static RequestedSportyBetOutcome? ResolveRequestedOutcome(string market, string prediction)
    {
        var normalizedMarket = market?.Trim().ToLowerInvariant() ?? string.Empty;
        var normalizedPrediction = prediction?.Trim().ToLowerInvariant() ?? string.Empty;

        if (normalizedMarket.Contains("btts") || normalizedPrediction.Contains("both teams") || normalizedPrediction.Contains("btts"))
        {
            return normalizedPrediction.Contains("no", StringComparison.OrdinalIgnoreCase)
                ? RequestedSportyBetOutcome.BttsNo
                : RequestedSportyBetOutcome.BttsYes;
        }

        if (normalizedMarket.Contains("under2.5") || normalizedPrediction.Contains("under 2.5") || normalizedPrediction.Contains("under2.5"))
        {
            return RequestedSportyBetOutcome.Under25;
        }

        if (normalizedMarket.Contains("over2.5") || normalizedPrediction.Contains("over 2.5") || normalizedPrediction.Contains("over2.5"))
        {
            return RequestedSportyBetOutcome.Over25;
        }

        if (normalizedPrediction == "x" || normalizedPrediction.Contains("draw"))
        {
            return RequestedSportyBetOutcome.Draw;
        }

        if (normalizedPrediction.Contains("away") || normalizedPrediction == "2")
        {
            return RequestedSportyBetOutcome.AwayWin;
        }

        if (normalizedPrediction.Contains("home") || normalizedPrediction == "1")
        {
            return RequestedSportyBetOutcome.HomeWin;
        }

        return normalizedMarket.Contains("1x2") ? RequestedSportyBetOutcome.HomeWin : null;
    }

    private static string ToCartMarket(Prediction prediction)
    {
        return prediction.PredictionCategory switch
        {
            "BothTeamsScore" => "BTTS",
            "Over2.5Goals" => "Over2.5",
            "Under2.5Goals" => "Under2.5",
            _ => "1X2"
        };
    }

    private static double ComputeTeamMatchScore(string expectedTeam, string actualTeam)
    {
        return ComputeTokenSimilarity(expectedTeam, actualTeam, TeamNoiseWords);
    }

    private static double ComputeLeagueMatchScore(string expectedLeague, string actualLeague)
    {
        return ComputeTokenSimilarity(expectedLeague, actualLeague, LeagueNoiseWords);
    }

    private static double ComputeTokenSimilarity(
        string? expected,
        string? actual,
        HashSet<string> noiseWords)
    {
        var normalizedExpected = NormalizeValue(expected);
        var normalizedActual = NormalizeValue(actual);
        if (string.IsNullOrWhiteSpace(normalizedExpected) || string.IsNullOrWhiteSpace(normalizedActual))
        {
            return 0d;
        }

        if (string.Equals(normalizedExpected, normalizedActual, StringComparison.Ordinal))
        {
            return 1d;
        }

        var compactExpected = normalizedExpected.Replace(" ", string.Empty, StringComparison.Ordinal);
        var compactActual = normalizedActual.Replace(" ", string.Empty, StringComparison.Ordinal);

        var expectedTokens = Tokenize(normalizedExpected, noiseWords);
        var actualTokens = Tokenize(normalizedActual, noiseWords);
        if (expectedTokens.Count == 0 || actualTokens.Count == 0)
        {
            return 0d;
        }

        var overlap = expectedTokens.Intersect(actualTokens, StringComparer.Ordinal).Count();
        var shorterCount = Math.Min(expectedTokens.Count, actualTokens.Count);
        var ratio = shorterCount == 0 ? 0d : overlap / (double)shorterCount;

        if (compactExpected.Contains(compactActual, StringComparison.Ordinal) ||
            compactActual.Contains(compactExpected, StringComparison.Ordinal))
        {
            ratio = Math.Max(ratio, 0.88d);
        }

        if (expectedTokens[0] == actualTokens[0] && overlap > 0)
        {
            ratio = Math.Max(ratio, 0.78d);
        }

        return Math.Min(1d, ratio);
    }

    private static string NormalizeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = NonWordRegex.Replace(value.ToLowerInvariant(), " ");
        var words = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => TokenSynonyms.TryGetValue(word, out var replacement) ? replacement : word)
            .ToArray();

        return string.Join(' ', words).Trim();
    }

    private static List<string> Tokenize(string normalizedValue, HashSet<string> noiseWords)
    {
        return normalizedValue
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !noiseWords.Contains(word))
            .ToList();
    }

    private static List<SportyBetFixture> DeduplicateFixturesByEventId(IEnumerable<SportyBetFixture> fixtures)
    {
        return fixtures
            .GroupBy(fixture => fixture.EventId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    private static string ExtractLeagueName(JsonElement fixtureElement)
    {
        if (!fixtureElement.TryGetProperty("sport", out var sport) ||
            !sport.TryGetProperty("category", out var category))
        {
            return string.Empty;
        }

        var country = category.TryGetProperty("name", out var categoryName) ? categoryName.GetString() ?? string.Empty : string.Empty;
        var tournament = category.TryGetProperty("tournament", out var tournamentElement) &&
                         tournamentElement.TryGetProperty("name", out var tournamentName)
            ? tournamentName.GetString() ?? string.Empty
            : string.Empty;

        if (string.IsNullOrWhiteSpace(country))
            return tournament;
        if (string.IsNullOrWhiteSpace(tournament))
            return country;

        return $"{country} - {tournament}";
    }

    private static double? TryParseProbability(JsonElement outcomeElement)
    {
        if (!outcomeElement.TryGetProperty("probability", out var probabilityElement))
            return null;

        if (probabilityElement.ValueKind == JsonValueKind.Number && probabilityElement.TryGetDouble(out var numericProbability))
            return numericProbability;

        if (probabilityElement.ValueKind == JsonValueKind.String &&
            double.TryParse(probabilityElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var stringProbability))
            return stringProbability;

        return null;
    }

    private static double? TryParseDecimalOdds(JsonElement outcomeElement)
    {
        foreach (var propertyName in new[] { "odds", "oddValue", "oddsValue", "price", "decimalOdds", "marketOdds" })
        {
            if (!outcomeElement.TryGetProperty(propertyName, out var oddsElement))
            {
                continue;
            }

            if (oddsElement.ValueKind == JsonValueKind.Number && oddsElement.TryGetDouble(out var numericOdds) && numericOdds > 1d)
            {
                return numericOdds;
            }

            if (oddsElement.ValueKind == JsonValueKind.String &&
                double.TryParse(oddsElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var stringOdds) &&
                stringOdds > 1d)
            {
                return stringOdds;
            }
        }

        return null;
    }
}

// ── Internal Models ──

public record SportyBetFixture
{
    public string EventId { get; init; } = "";
    public string League { get; init; } = "";
    public string HomeTeam { get; init; } = "";
    public string AwayTeam { get; init; } = "";
    public DateTime? MatchTimeUtc { get; init; }
    public string HomeOutcomeId { get; init; } = "";
    public string DrawOutcomeId { get; init; } = "";
    public string AwayOutcomeId { get; init; } = "";
    public string BttsYesOutcomeId { get; init; } = "";
    public string BttsNoOutcomeId { get; init; } = "";
    public string Over25OutcomeId { get; init; } = "";
    public string Under25OutcomeId { get; init; } = "";
    public double? HomeProbability { get; init; }
    public double? HomeOdds { get; init; }
    public double? DrawProbability { get; init; }
    public double? DrawOdds { get; init; }
    public double? AwayProbability { get; init; }
    public double? AwayOdds { get; init; }
    public double? Over25Probability { get; init; }
    public double? Over25Odds { get; init; }
    public double? Under25Probability { get; init; }
    public double? Under25Odds { get; init; }
    public double? BttsYesProbability { get; init; }
    public double? BttsYesOdds { get; init; }
    public double? BttsNoProbability { get; init; }
    public double? BttsNoOdds { get; init; }
}

public record SportyBetOutcome
{
    public string EventId { get; init; } = "";
    public string OutcomeId { get; init; } = "";
    public string MarketId { get; init; } = "1";
    public string? Specifier { get; init; }
    public string HomeTeam { get; init; } = "";
    public string AwayTeam { get; init; } = "";
}

internal sealed record ResolvedBookingSelection(
    BookingSelection OriginalSelection,
    int? PredictionId,
    string HomeTeam,
    string AwayTeam,
    string League,
    string Market,
    string Prediction,
    DateTime? MatchDateTimeUtc,
    DateOnly? MatchLocalDate,
    RequestedSportyBetOutcome? RequestedOutcome)
{
    public string SelectionLabel => $"{HomeTeam} vs {AwayTeam} ({Prediction})";
}

internal sealed record BookingSelectionResolution(
    BookingSelectionMatchStatus Status,
    SportyBetOutcome? Outcome,
    SportyBetFixture? MatchedFixture);

internal sealed record FixtureMatchCandidate(
    SportyBetFixture Fixture,
    double Score,
    TimeSpan? KickoffDelta,
    bool IsCandidate)
{
    public static FixtureMatchCandidate NotCandidate(SportyBetFixture fixture) => new(fixture, 0d, null, false);
}

internal enum BookingSelectionMatchStatus
{
    Matched,
    OutsideTodayWindow,
    NoFixtureFound,
    AmbiguousFixture,
    MarketUnavailable
}

internal enum RequestedSportyBetOutcome
{
    HomeWin,
    Draw,
    AwayWin,
    BttsYes,
    BttsNo,
    Over25,
    Under25
}
