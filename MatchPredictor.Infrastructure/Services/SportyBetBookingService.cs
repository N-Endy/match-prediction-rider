using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MatchPredictor.Infrastructure.Services;

public class SportyBetBookingService : ISportyBetBookingService, ISourceMarketPricingService
{
    private const string PricingClientName = "SportyBetPricing";
    private const string BookingClientName = "SportyBetBooking";
    private const int DefaultPricingPageSize = 100;
    private const int DefaultBookingPageSize = 100;
    private const int DefaultPricingMaxPages = 10;
    private const int DefaultBookingMaxPages = 10;
    private const string DefaultTennisUpcomingMarketId = "1";
    private const string TotalSetsBookingMarket = "OverUnderSets";
    private const string SetHandicapBookingMarket = "SetHandicap";
    private const string MatchWinnerBookingMarket = "MatchWinner";
    private const double MinimumDirectionalTeamScore = 0.72;
    private const double MinimumConfidentMatchScore = 1.55;
    private const double AmbiguousScoreGap = 0.12;
    private static readonly TimeSpan TightKickoffWindow = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan LooseKickoffWindow = TimeSpan.FromMinutes(90);
    private static readonly TimeSpan MaximumKickoffWindow = TimeSpan.FromHours(6);
    private static readonly TimeSpan FullFixtureCacheTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan BackupFixtureCacheTtl = TimeSpan.FromHours(6);
    private static readonly Regex NonWordRegex = new("[^a-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex HandicapOutcomeRegex = new(@"^(?<side>.+?)\s*\((?<line>[+-]?\d+(?:\.\d+)?)\)$", RegexOptions.Compiled);
    private static readonly Regex PredictionHandicapRegex = new(@"^(Home|Away)\s+([+-]?\d+(?:\.\d+)?)\s+Sets?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NumericLineRegex = new(@"[+-]?\d+(?:\.\d+)?", RegexOptions.Compiled);
    private static readonly HashSet<string> TeamNoiseWords =
    [
        "fc", "cf", "sc", "afc", "club", "the", "de", "da", "do", "cd", "ud", "ac", "as", "fk", "sk", "nk", "if", "bk"
    ];
    private static readonly HashSet<string> LeagueNoiseWords =
    [
        "league", "division", "group", "round", "stage", "play", "offs", "open", "tour", "masters"
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
        _logger.LogInformation("Starting SportyBet booking process for {SelectionCount} selection(s).", selections.Count);
        
        if (selections.Count == 0)
        {
            _logger.LogWarning("SportyBet booking attempted with 0 selections.");
            return new BookingResult { Success = false, Message = "No games selected." };
        }

        var baseUrl = _configuration["SportyBet:BaseUrl"] ?? "https://www.sportybet.com";
        var tennisSportId = _configuration["SportyBet:TennisSportId"] ?? "sr:sport:5";
        var matchWinnerMarketId = _configuration["SportyBet:MatchWinnerMarketId"] ?? DefaultTennisUpcomingMarketId;
        var todayLocalDate = DateTimeProvider.GetLocalDate();

        try
        {
            var canonicalSelections = await BuildCanonicalSelectionsAsync(selections, CancellationToken.None);
            _logger.LogInformation("Resolved {CanonicalCount} canonical matches for booking.", canonicalSelections.Count);
            
            var matchableSelections = canonicalSelections
                .Where(selection => !selection.MatchLocalDate.HasValue || selection.MatchLocalDate.Value == todayLocalDate)
                .ToList();
            var warnings = canonicalSelections
                .Where(selection => selection.MatchLocalDate.HasValue && selection.MatchLocalDate.Value != todayLocalDate)
                .Select(selection => BuildSelectionWarning(selection, BookingSelectionMatchStatus.OutsideTodayWindow))
                .ToList();

            if (matchableSelections.Count == 0)
            {
                _logger.LogWarning("No matchable selections for today's SportyBet games.");
                return BuildBookingFailureResult(
                    "All selected matches fall outside today's SportyBet tennis card.",
                    bookedCount: 0,
                    totalSelections: selections.Count,
                    warnings);
            }

            _logger.LogInformation("Fetching SportyBet fixtures to resolve {MatchableCount} matchable selections.", matchableSelections.Count);
            var fixtures = await FetchTodayFixturesAsync(
                baseUrl,
                tennisSportId,
                matchWinnerMarketId,
                CancellationToken.None,
                useBookingClient: true,
                targetedSelections: matchableSelections);

            if (fixtures.Count == 0)
            {
                _logger.LogWarning("Could not retrieve SportyBet fixtures to proceed with booking.");
                return BuildBookingFailureResult(
                    "Could not fetch today's tennis fixtures from SportyBet.",
                    bookedCount: 0,
                    totalSelections: selections.Count,
                    warnings);
            }

            var selectedOutcomes = new List<SportyBetOutcome>();
            foreach (var selection in matchableSelections)
            {
                var resolution = ResolveSelection(fixtures, selection);
                if (resolution.Outcome is not null)
                {
                    selectedOutcomes.Add(resolution.Outcome);
                    continue;
                }

                warnings.Add(BuildSelectionWarning(selection, resolution.Status, resolution.MatchedFixture));
            }

            if (selectedOutcomes.Count == 0)
            {
                _logger.LogWarning("Failed to match any selection with available SportyBet odds.");
                return BuildBookingFailureResult(
                    "None of the selected tennis picks could be booked on SportyBet today.",
                    bookedCount: 0,
                    totalSelections: selections.Count,
                    warnings);
            }

            _logger.LogInformation("Found {OutcomeCount} outcomes to book. Generating code.", selectedOutcomes.Count);
            var (bookingCode, bookingUrl) = await CreateBookingCodeAsync(selectedOutcomes, baseUrl);
            var skippedCount = selections.Count - selectedOutcomes.Count;
            if (!string.IsNullOrWhiteSpace(bookingCode))
            {
                _logger.LogInformation("Successfully generated SportyBet booking code: {BookingCode}.", bookingCode);
                return new BookingResult
                {
                    Success = true,
                    BookingCode = bookingCode,
                    BookingUrl = bookingUrl ?? string.Empty,
                    Message = skippedCount > 0
                        ? $"Booked {selectedOutcomes.Count}/{selections.Count} tennis picks. Skipped picks are listed below."
                        : $"Booked {selectedOutcomes.Count}/{selections.Count} tennis picks.",
                    BookedCount = selectedOutcomes.Count,
                    SkippedCount = skippedCount,
                    Warnings = warnings
                };
            }

            _logger.LogWarning("Failed to generate booking code despite having {OutcomeCount} outcomes.", selectedOutcomes.Count);
            return BuildBookingFailureResult(
                $"Found {selectedOutcomes.Count} tennis selections but could not generate a SportyBet booking code.",
                bookedCount: selectedOutcomes.Count,
                totalSelections: selections.Count,
                warnings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during SportyBet tennis booking via API.");
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
        var tennisSportId = _configuration["SportyBet:TennisSportId"] ?? "sr:sport:5";
        var matchWinnerMarketId = _configuration["SportyBet:MatchWinnerMarketId"] ?? DefaultTennisUpcomingMarketId;

        var fixtures = await FetchTodayFixturesAsync(baseUrl, tennisSportId, matchWinnerMarketId, ct, useBookingClient: false);
        return fixtures.Select(fixture => new SourceMarketFixture
        {
            EventId = fixture.EventId,
            League = fixture.League,
            HomeTeam = fixture.HomeTeam,
            AwayTeam = fixture.AwayTeam,
            MatchTimeUtc = fixture.MatchTimeUtc,
            HomeWinProbability = fixture.HomeProbability,
            HomeWinOdds = fixture.HomeOdds,
            AwayWinProbability = fixture.AwayProbability,
            AwayWinOdds = fixture.AwayOdds,
            MarketSelections = fixture.MarketSelections
                .Select(selection => new SourceMarketSelection
                {
                    Market = selection.Market,
                    Prediction = selection.Prediction,
                    MarketId = selection.MarketId,
                    Specifier = selection.Specifier,
                    OutcomeId = selection.OutcomeId,
                    Descriptor = selection.Descriptor,
                    Probability = selection.Probability,
                    DecimalOdds = selection.DecimalOdds
                })
                .ToList()
        }).ToList();
    }

    private async Task<List<SportyBetFixture>> FetchTodayFixturesAsync(
        string baseUrl,
        string tennisSportId,
        string matchWinnerMarketId,
        CancellationToken ct,
        bool useBookingClient,
        IReadOnlyCollection<ResolvedBookingSelection>? targetedSelections = null)
    {
        var cacheKey = $"sportybet_tennis_fixtures_v2_{DateTime.UtcNow:yyyyMMdd}";
        var backupCacheKey = $"{cacheKey}_backup";

        try
        {
            var cachedData = await _cache.GetStringAsync(cacheKey, ct);
            if (!string.IsNullOrWhiteSpace(cachedData))
            {
                var cachedFixtures = JsonSerializer.Deserialize<List<SportyBetFixture>>(cachedData) ?? new List<SportyBetFixture>();
                var deduplicated = await HydrateMissingWinnerMarketsAsync(
                    DeduplicateFixturesByEventId(cachedFixtures),
                    baseUrl,
                    useBookingClient,
                    ct);
                if (targetedSelections is null || CanResolveSelections(deduplicated, targetedSelections))
                {
                    return deduplicated;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read SportyBet tennis fixtures from cache.");
        }

        var fixturesByEventId = new Dictionary<string, SportyBetFixture>(StringComparer.Ordinal);
        var client = CreateHttpClient(useBookingClient ? BookingClientName : PricingClientName);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var pageSize = ResolvePageSize(useBookingClient);
        var maxPages = ResolveMaxPages(useBookingClient);

        for (var page = 1; page <= maxPages; page++)
        {
            try
            {
                var url = $"{baseUrl}/api/ng/factsCenter/pcUpcomingEvents" +
                          $"?sportId={Uri.EscapeDataString(tennisSportId)}" +
                          $"&marketId={Uri.EscapeDataString(matchWinnerMarketId)}" +
                          $"&pageSize={pageSize}&pageNum={page}" +
                          $"&todayGames=true&timeline=2.9&_t={timestamp}";

                var response = await client.GetAsync(url, ct);
                var responseBody = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("SportyBet tennis page {Page} returned {Status}.", page, response.StatusCode);
                    break;
                }

                using var document = JsonDocument.Parse(responseBody);
                if (!document.RootElement.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("tournaments", out var tournaments))
                {
                    break;
                }

                var tournamentCount = 0;
                foreach (var tournament in tournaments.EnumerateArray())
                {
                    tournamentCount++;
                    if (!tournament.TryGetProperty("events", out var events))
                    {
                        continue;
                    }

                    foreach (var ev in events.EnumerateArray())
                    {
                        try
                        {
                            var homeTeam = ev.GetProperty("homeTeamName").GetString() ?? string.Empty;
                            var awayTeam = ev.GetProperty("awayTeamName").GetString() ?? string.Empty;
                            var eventId = ev.GetProperty("eventId").GetString() ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(eventId))
                            {
                                continue;
                            }

                            var kickoffTimeUtc = ev.TryGetProperty("estimateStartTime", out var estimateStartTimeElement) &&
                                                 estimateStartTimeElement.TryGetInt64(out var estimateStartTime)
                                ? DateTimeOffset.FromUnixTimeMilliseconds(estimateStartTime).UtcDateTime
                                : (DateTime?)null;

                            var league = ExtractLeagueName(ev);
                            var fixture = ParseFixture(ev, eventId, homeTeam, awayTeam, league, kickoffTimeUtc, matchWinnerMarketId);
                            fixturesByEventId[eventId] = fixture;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Skipping SportyBet tennis fixture due to parse error.");
                        }
                    }
                }

                var currentFixtures = fixturesByEventId.Values.ToList();
                if (targetedSelections is { Count: > 0 } && CanResolveSelections(currentFixtures, targetedSelections))
                {
                    break;
                }

                if (tournamentCount == 0)
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error fetching SportyBet tennis fixtures page {Page}.", page);
                break;
            }
        }

        var deduplicatedFixtures = await HydrateMissingWinnerMarketsAsync(
            DeduplicateFixturesByEventId(fixturesByEventId.Values),
            baseUrl,
            useBookingClient,
            ct);
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
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cache SportyBet tennis fixtures.");
            }
        }
        else if (useBookingClient)
        {
            try
            {
                var backupCachedData = await _cache.GetStringAsync(backupCacheKey, ct);
                if (!string.IsNullOrWhiteSpace(backupCachedData))
                {
                    var backupFixtures = JsonSerializer.Deserialize<List<SportyBetFixture>>(backupCachedData) ?? new List<SportyBetFixture>();
                    return await HydrateMissingWinnerMarketsAsync(
                        DeduplicateFixturesByEventId(backupFixtures),
                        baseUrl,
                        useBookingClient,
                        ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read stale SportyBet tennis fixture cache.");
            }
        }

        return deduplicatedFixtures;
    }

    private static SportyBetFixture ParseFixture(
        JsonElement eventElement,
        string eventId,
        string homeTeam,
        string awayTeam,
        string league,
        DateTime? kickoffTimeUtc,
        string preferredMarketId)
    {
        var fixture = new SportyBetFixture
        {
            EventId = eventId,
            League = league,
            HomeTeam = homeTeam,
            AwayTeam = awayTeam,
            MatchTimeUtc = kickoffTimeUtc,
            WinnerMarketId = preferredMarketId,
            MarketSelections = []
        };

        if (!eventElement.TryGetProperty("markets", out var markets))
        {
            return fixture;
        }

        var winnerMarket = SelectMatchWinnerMarket(markets, preferredMarketId);
        if (winnerMarket is null)
        {
            return fixture;
        }

        var marketElement = winnerMarket.Value;
        var marketId = marketElement.TryGetProperty("id", out var marketIdElement)
            ? marketIdElement.GetString() ?? string.Empty
            : string.Empty;
        var outcomes = marketElement.TryGetProperty("outcomes", out var outcomesElement)
            ? outcomesElement.EnumerateArray().Select(ParseOutcome).ToList()
            : new List<ParsedOutcome>();
        if (outcomes.Count == 0)
        {
            return fixture;
        }

        var homeOutcome = outcomes.FirstOrDefault(outcome => IsHomeOutcome(outcome, homeTeam, awayTeam));
        var awayOutcome = outcomes.FirstOrDefault(outcome => IsAwayOutcome(outcome, homeTeam, awayTeam));

        if ((homeOutcome is null || awayOutcome is null) && outcomes.Count == 2)
        {
            homeOutcome ??= outcomes[0];
            awayOutcome ??= outcomes[1];
        }

        var marketSelections = new List<SportyBetFixtureSelection>();

        if (homeOutcome is not null)
        {
            fixture = fixture with
            {
                WinnerMarketId = string.IsNullOrWhiteSpace(marketId) ? fixture.WinnerMarketId : marketId,
                HomeOutcomeId = homeOutcome.OutcomeId,
                HomeProbability = ResolveProbability(homeOutcome),
                HomeOdds = homeOutcome.DecimalOdds
            };

            marketSelections.Add(new SportyBetFixtureSelection(
                MatchWinnerBookingMarket,
                "Home Win",
                string.IsNullOrWhiteSpace(marketId) ? fixture.WinnerMarketId : marketId,
                null,
                homeOutcome.OutcomeId,
                homeOutcome.Descriptor,
                ResolveProbability(homeOutcome),
                homeOutcome.DecimalOdds));
        }

        if (awayOutcome is not null)
        {
            fixture = fixture with
            {
                WinnerMarketId = string.IsNullOrWhiteSpace(marketId) ? fixture.WinnerMarketId : marketId,
                AwayOutcomeId = awayOutcome.OutcomeId,
                AwayProbability = ResolveProbability(awayOutcome),
                AwayOdds = awayOutcome.DecimalOdds
            };

            marketSelections.Add(new SportyBetFixtureSelection(
                MatchWinnerBookingMarket,
                "Away Win",
                string.IsNullOrWhiteSpace(marketId) ? fixture.WinnerMarketId : marketId,
                null,
                awayOutcome.OutcomeId,
                awayOutcome.Descriptor,
                ResolveProbability(awayOutcome),
                awayOutcome.DecimalOdds));
        }

        marketSelections.AddRange(ParseAdditionalSelections(markets, homeTeam, awayTeam));
        return fixture with { MarketSelections = DeduplicateMarketSelections(marketSelections) };
    }

    private async Task<List<SportyBetFixture>> HydrateMissingWinnerMarketsAsync(
        List<SportyBetFixture> fixtures,
        string baseUrl,
        bool useBookingClient,
        CancellationToken ct)
    {
        if (fixtures.Count == 0)
        {
            return fixtures;
        }

        var client = CreateHttpClient(useBookingClient ? BookingClientName : PricingClientName);
        var hydratedFixtures = new List<SportyBetFixture>(fixtures.Count);

        foreach (var chunk in fixtures.Chunk(5))
        {
            var tasks = chunk.Select(async fixture =>
            {
                if (!NeedsWinnerMarketHydration(fixture))
                {
                    return fixture;
                }

                try
                {
                    return await FetchEventFixtureDetailAsync(client, baseUrl, fixture, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to hydrate SportyBet tennis market data for {EventId}.", fixture.EventId);
                    return fixture;
                }
            });

            var results = await Task.WhenAll(tasks);
            hydratedFixtures.AddRange(results);
        }

        return hydratedFixtures;
    }

    private async Task<SportyBetFixture> FetchEventFixtureDetailAsync(
        HttpClient client,
        string baseUrl,
        SportyBetFixture fixture,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fixture.EventId))
        {
            return fixture;
        }

        var response = await client.GetAsync(
            $"{baseUrl}/api/ng/factsCenter/event?eventId={Uri.EscapeDataString(fixture.EventId)}",
            ct);
        if (!response.IsSuccessStatusCode)
        {
            return fixture;
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(responseBody);
        if (!document.RootElement.TryGetProperty("data", out var eventData))
        {
            return fixture;
        }

        var homeTeam = eventData.TryGetProperty("homeTeamName", out var homeTeamElement)
            ? homeTeamElement.GetString() ?? fixture.HomeTeam
            : fixture.HomeTeam;
        var awayTeam = eventData.TryGetProperty("awayTeamName", out var awayTeamElement)
            ? awayTeamElement.GetString() ?? fixture.AwayTeam
            : fixture.AwayTeam;
        var eventId = eventData.TryGetProperty("eventId", out var eventIdElement)
            ? eventIdElement.GetString() ?? fixture.EventId
            : fixture.EventId;
        var kickoffTimeUtc = eventData.TryGetProperty("estimateStartTime", out var estimateStartTimeElement) &&
                             estimateStartTimeElement.TryGetInt64(out var estimateStartTime)
            ? DateTimeOffset.FromUnixTimeMilliseconds(estimateStartTime).UtcDateTime
            : fixture.MatchTimeUtc;

        var hydratedFixture = ParseFixture(
            eventData,
            eventId,
            homeTeam,
            awayTeam,
            string.IsNullOrWhiteSpace(ExtractLeagueName(eventData)) ? fixture.League : ExtractLeagueName(eventData),
            kickoffTimeUtc,
            fixture.WinnerMarketId);

        return hydratedFixture with
        {
            EventId = fixture.EventId,
            League = string.IsNullOrWhiteSpace(hydratedFixture.League) ? fixture.League : hydratedFixture.League,
            HomeTeam = string.IsNullOrWhiteSpace(hydratedFixture.HomeTeam) ? fixture.HomeTeam : hydratedFixture.HomeTeam,
            AwayTeam = string.IsNullOrWhiteSpace(hydratedFixture.AwayTeam) ? fixture.AwayTeam : hydratedFixture.AwayTeam,
            MatchTimeUtc = hydratedFixture.MatchTimeUtc ?? fixture.MatchTimeUtc
        };
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

    private async Task<(string? Code, string? Url)> CreateBookingCodeAsync(List<SportyBetOutcome> outcomes, string baseUrl)
    {
        var client = CreateHttpClient(BookingClientName);

        var selections = outcomes.Select(outcome => new
        {
            eventId = outcome.EventId,
            marketId = outcome.MarketId,
            specifier = outcome.Specifier,
            outcomeId = outcome.OutcomeId
        }).ToArray();

        var payload = new { selections };
        var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var response = await client.PostAsync($"{baseUrl}/api/ng/orders/share", body);
        var responseBody = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("SportyBet tennis booking API returned {Status}.", response.StatusCode);
            return (null, null);
        }

        using var doc = JsonDocument.Parse(responseBody);
        if (!doc.RootElement.TryGetProperty("data", out var data))
        {
            return (null, null);
        }

        string? code = null;
        string? url = null;
        if (data.TryGetProperty("shareCode", out var shareCode))
        {
            code = shareCode.GetString();
        }
        else if (data.TryGetProperty("bookCode", out var bookCode))
        {
            code = bookCode.GetString();
        }
        else if (data.TryGetProperty("code", out var genericCode))
        {
            code = genericCode.GetString();
        }
        else if (data.ValueKind == JsonValueKind.String)
        {
            code = data.GetString();
        }

        if (data.TryGetProperty("shareURL", out var shareUrl))
        {
            url = shareUrl.GetString();
        }

        return (code, url);
    }

    private HttpClient CreateHttpClient(string clientName)
    {
        var client = _httpClientFactory.CreateClient(clientName);
        var baseUrl = _configuration["SportyBet:BaseUrl"] ?? "https://www.sportybet.com";
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
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
        return Math.Clamp(_configuration.GetValue<int?>(configKey) ?? defaultSize, 10, 200);
    }

    private int ResolveMaxPages(bool useBookingClient)
    {
        var configKey = useBookingClient ? "SportyBet:BookingMaxPages" : "SportyBet:PricingMaxPages";
        var defaultPages = useBookingClient ? DefaultBookingMaxPages : DefaultPricingMaxPages;
        return Math.Clamp(_configuration.GetValue<int?>(configKey) ?? defaultPages, 1, 30);
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

        return TryCreateOutcome(bestCandidate.Fixture, selection.RequestedOutcome, out var outcome)
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
            BookingSelectionMatchStatus.OutsideTodayWindow => $"{label}: outside today's SportyBet tennis card.",
            BookingSelectionMatchStatus.NoFixtureFound => $"{label}: no SportyBet tennis fixture found for today's card.",
            BookingSelectionMatchStatus.AmbiguousFixture => $"{label}: fixture match was ambiguous, so it was skipped.",
            BookingSelectionMatchStatus.MarketUnavailable => matchedFixture is not null
                ? $"{label}: SportyBet found {matchedFixture.HomeTeam} vs {matchedFixture.AwayTeam}, but that exact tennis market or line is unavailable there."
                : $"{label}: requested tennis market is unavailable on SportyBet.",
            _ => $"{label}: skipped."
        };
    }

    private static bool TryCreateOutcome(
        SportyBetFixture fixture,
        RequestedSportyBetOutcome requestedOutcome,
        out SportyBetOutcome outcome)
    {
        var matchedSelection = fixture.MarketSelections.FirstOrDefault(selection =>
            string.Equals(selection.Market, requestedOutcome.Market, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(selection.Prediction, requestedOutcome.Prediction, StringComparison.OrdinalIgnoreCase));

        if (matchedSelection is not null)
        {
            outcome = new SportyBetOutcome
            {
                EventId = fixture.EventId,
                HomeTeam = fixture.HomeTeam,
                AwayTeam = fixture.AwayTeam,
                MarketId = matchedSelection.MarketId,
                OutcomeId = matchedSelection.OutcomeId,
                Specifier = matchedSelection.Specifier
            };

            return true;
        }

        outcome = new SportyBetOutcome
        {
            EventId = fixture.EventId,
            HomeTeam = fixture.HomeTeam,
            AwayTeam = fixture.AwayTeam,
            MarketId = fixture.WinnerMarketId
        };

        switch (requestedOutcome.LegacyOutcome)
        {
            case LegacyRequestedSportyBetOutcome.HomeWin when !string.IsNullOrWhiteSpace(fixture.HomeOutcomeId):
                outcome = outcome with { OutcomeId = fixture.HomeOutcomeId };
                return true;
            case LegacyRequestedSportyBetOutcome.AwayWin when !string.IsNullOrWhiteSpace(fixture.AwayOutcomeId):
                outcome = outcome with { OutcomeId = fixture.AwayOutcomeId };
                return true;
            default:
                return false;
        }
    }

    private static RequestedSportyBetOutcome? ResolveRequestedOutcome(string market, string prediction)
    {
        if (!TryNormalizeMarket(market, out var normalizedMarket))
        {
            return null;
        }

        if (!TryNormalizePrediction(normalizedMarket, prediction, out var normalizedPrediction))
        {
            return null;
        }

        var legacyOutcome = normalizedMarket == MatchWinnerBookingMarket
            ? normalizedPrediction switch
            {
                "Home Win" => LegacyRequestedSportyBetOutcome.HomeWin,
                "Away Win" => LegacyRequestedSportyBetOutcome.AwayWin,
                _ => (LegacyRequestedSportyBetOutcome?)null
            }
            : null;

        return new RequestedSportyBetOutcome(normalizedMarket, normalizedPrediction, legacyOutcome);
    }

    private static string ToCartMarket(Prediction prediction)
    {
        return prediction.PredictionCategory switch
        {
            "MatchWinner" => MatchWinnerBookingMarket,
            "OverUnderSets" => TotalSetsBookingMarket,
            "SetHandicap" => SetHandicapBookingMarket,
            _ => prediction.PredictionCategory
        };
    }

    private static bool IsHomeOutcome(ParsedOutcome outcome, string homeTeam, string awayTeam)
    {
        var descriptor = outcome.Descriptor;
        if (string.Equals(outcome.OutcomeId, "1", StringComparison.Ordinal))
        {
            return true;
        }

        if (descriptor.Contains("home", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ComputeTeamMatchScore(descriptor, homeTeam) >= 0.85d &&
               ComputeTeamMatchScore(descriptor, awayTeam) < 0.75d;
    }

    private static bool IsAwayOutcome(ParsedOutcome outcome, string homeTeam, string awayTeam)
    {
        var descriptor = outcome.Descriptor;
        if (string.Equals(outcome.OutcomeId, "2", StringComparison.Ordinal) ||
            string.Equals(outcome.OutcomeId, "3", StringComparison.Ordinal))
        {
            return true;
        }

        if (descriptor.Contains("away", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ComputeTeamMatchScore(descriptor, awayTeam) >= 0.85d &&
               ComputeTeamMatchScore(descriptor, homeTeam) < 0.75d;
    }

    private static ParsedOutcome ParseOutcome(JsonElement outcomeElement)
    {
        var outcomeId = outcomeElement.TryGetProperty("id", out var idElement)
            ? idElement.GetString() ?? string.Empty
            : string.Empty;
        var descriptor = ExtractOutcomeDescriptor(outcomeElement);
        var decimalOdds = TryParseDecimalOdds(outcomeElement);
        var probability = TryParseProbability(outcomeElement);

        return new ParsedOutcome(outcomeId, descriptor, probability, decimalOdds);
    }

    private static string ExtractOutcomeDescriptor(JsonElement outcomeElement)
    {
        foreach (var propertyName in new[] { "desc", "description", "name", "outcomeName", "title" })
        {
            if (outcomeElement.TryGetProperty(propertyName, out var property))
            {
                var value = property.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }

        return string.Empty;
    }

    private static double? ResolveProbability(ParsedOutcome outcome)
    {
        if (outcome.Probability is > 0d)
        {
            return outcome.Probability;
        }

        return outcome.DecimalOdds is > 1d
            ? 1d / outcome.DecimalOdds.Value
            : null;
    }

    private static List<SportyBetFixtureSelection> ParseAdditionalSelections(
        JsonElement marketsElement,
        string homeTeam,
        string awayTeam)
    {
        var selections = new List<SportyBetFixtureSelection>();

        foreach (var market in marketsElement.EnumerateArray())
        {
            var marketId = market.TryGetProperty("id", out var marketIdElement)
                ? marketIdElement.GetString() ?? string.Empty
                : string.Empty;
            var specifier = market.TryGetProperty("specifier", out var specifierElement)
                ? specifierElement.GetString()
                : null;
            var outcomes = market.TryGetProperty("outcomes", out var outcomesElement)
                ? outcomesElement.EnumerateArray().Select(ParseOutcome).ToList()
                : new List<ParsedOutcome>();

            if (outcomes.Count == 0)
            {
                continue;
            }

            if (IsTotalSetsMarket(market))
            {
                foreach (var outcome in outcomes)
                {
                    if (!TryBuildTotalSetsPrediction(outcome, specifier, out var prediction))
                    {
                        continue;
                    }

                    selections.Add(new SportyBetFixtureSelection(
                        TotalSetsBookingMarket,
                        prediction,
                        marketId,
                        specifier,
                        outcome.OutcomeId,
                        outcome.Descriptor,
                        ResolveProbability(outcome),
                        outcome.DecimalOdds));
                }

                continue;
            }

            if (IsSetHandicapMarket(market))
            {
                foreach (var outcome in outcomes)
                {
                    if (!TryBuildSetHandicapPrediction(outcome, homeTeam, awayTeam, out var prediction))
                    {
                        continue;
                    }

                    selections.Add(new SportyBetFixtureSelection(
                        SetHandicapBookingMarket,
                        prediction,
                        marketId,
                        specifier,
                        outcome.OutcomeId,
                        outcome.Descriptor,
                        ResolveProbability(outcome),
                        outcome.DecimalOdds));
                }
            }
        }

        return selections;
    }

    private static List<SportyBetFixtureSelection> DeduplicateMarketSelections(IEnumerable<SportyBetFixtureSelection> selections)
    {
        return selections
            .GroupBy(
                selection => $"{selection.Market}|{selection.Prediction}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static bool IsTotalSetsMarket(JsonElement market)
    {
        var description = GetMarketText(market, "desc");
        var name = GetMarketText(market, "name");
        var guide = GetMarketText(market, "marketGuide");
        var title = GetMarketText(market, "title");
        var normalized = $"{description} {name} {guide} {title}".Trim();

        return normalized.Contains("total sets", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Contains("exact sets", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSetHandicapMarket(JsonElement market)
    {
        var description = GetMarketText(market, "desc");
        var name = GetMarketText(market, "name");
        var guide = GetMarketText(market, "marketGuide");
        var normalized = $"{description} {name} {guide}".Trim();

        return normalized.Contains("set handicap", StringComparison.OrdinalIgnoreCase) ||
               guide.Contains("set spread", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryBuildTotalSetsPrediction(ParsedOutcome outcome, string? specifier, out string prediction)
    {
        prediction = string.Empty;

        var descriptor = outcome.Descriptor?.Trim() ?? string.Empty;
        if (!(descriptor.StartsWith("Over", StringComparison.OrdinalIgnoreCase) ||
              descriptor.StartsWith("Under", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var line = TryParseNumericLine(descriptor) ?? TryParseNumericLine(specifier);
        if (!line.HasValue)
        {
            return false;
        }

        var side = descriptor.StartsWith("Over", StringComparison.OrdinalIgnoreCase) ? "Over" : "Under";
        prediction = $"{side} {line.Value:0.0} Sets";
        return true;
    }

    private static bool TryBuildSetHandicapPrediction(
        ParsedOutcome outcome,
        string homeTeam,
        string awayTeam,
        out string prediction)
    {
        prediction = string.Empty;
        var descriptor = outcome.Descriptor?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(descriptor))
        {
            return false;
        }

        var match = HandicapOutcomeRegex.Match(descriptor);
        if (!match.Success)
        {
            return false;
        }

        var rawSide = match.Groups["side"].Value.Trim();
        var normalizedSide = ResolveHandicapSide(rawSide, homeTeam, awayTeam);
        if (normalizedSide is null ||
            !double.TryParse(match.Groups["line"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var line))
        {
            return false;
        }

        prediction = $"{normalizedSide} {line:+0.0;-0.0} Sets";
        return true;
    }

    private static string? ResolveHandicapSide(string rawSide, string homeTeam, string awayTeam)
    {
        if (rawSide.Contains("home", StringComparison.OrdinalIgnoreCase))
        {
            return "Home";
        }

        if (rawSide.Contains("away", StringComparison.OrdinalIgnoreCase))
        {
            return "Away";
        }

        if (ComputeTeamMatchScore(rawSide, homeTeam) >= 0.85d &&
            ComputeTeamMatchScore(rawSide, awayTeam) < 0.75d)
        {
            return "Home";
        }

        if (ComputeTeamMatchScore(rawSide, awayTeam) >= 0.85d &&
            ComputeTeamMatchScore(rawSide, homeTeam) < 0.75d)
        {
            return "Away";
        }

        return null;
    }

    private static bool TryNormalizeMarket(string market, out string normalizedMarket)
    {
        var value = market?.Trim().ToLowerInvariant() ?? string.Empty;
        normalizedMarket = string.Empty;

        if (value == MatchWinnerBookingMarket.ToLowerInvariant())
        {
            normalizedMarket = MatchWinnerBookingMarket;
        }
        else if (value == TotalSetsBookingMarket.ToLowerInvariant())
        {
            normalizedMarket = TotalSetsBookingMarket;
        }
        else if (value == SetHandicapBookingMarket.ToLowerInvariant())
        {
            normalizedMarket = SetHandicapBookingMarket;
        }
        else if (value.Contains("winner", StringComparison.Ordinal) ||
                 value.Contains("matchwinner", StringComparison.Ordinal) ||
                 value.Contains("1x2", StringComparison.Ordinal) ||
                 value.Contains("moneyline", StringComparison.Ordinal))
        {
            normalizedMarket = MatchWinnerBookingMarket;
        }
        else if (value.Contains("overunder", StringComparison.Ordinal) ||
                 value.Contains("totalsets", StringComparison.Ordinal) ||
                 value.Contains("total sets", StringComparison.Ordinal) ||
                 value.Contains("sets total", StringComparison.Ordinal))
        {
            normalizedMarket = TotalSetsBookingMarket;
        }
        else if (value.Contains("sethandicap", StringComparison.Ordinal) ||
                 value.Contains("set handicap", StringComparison.Ordinal))
        {
            normalizedMarket = SetHandicapBookingMarket;
        }

        return !string.IsNullOrWhiteSpace(normalizedMarket);
    }

    private static bool TryNormalizePrediction(string market, string prediction, out string normalizedPrediction)
    {
        normalizedPrediction = string.Empty;
        var trimmedPrediction = prediction?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedPrediction))
        {
            return false;
        }

        switch (market)
        {
            case MatchWinnerBookingMarket:
            {
                if (trimmedPrediction.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                    trimmedPrediction.Contains("home", StringComparison.OrdinalIgnoreCase))
                {
                    normalizedPrediction = "Home Win";
                    return true;
                }

                if (trimmedPrediction.Equals("2", StringComparison.OrdinalIgnoreCase) ||
                    trimmedPrediction.Contains("away", StringComparison.OrdinalIgnoreCase))
                {
                    normalizedPrediction = "Away Win";
                    return true;
                }

                return false;
            }

            case TotalSetsBookingMarket:
            {
                var side = trimmedPrediction.StartsWith("Over", StringComparison.OrdinalIgnoreCase)
                    ? "Over"
                    : trimmedPrediction.StartsWith("Under", StringComparison.OrdinalIgnoreCase)
                        ? "Under"
                        : null;
                var line = TryParseNumericLine(trimmedPrediction);
                if (side is null || !line.HasValue)
                {
                    return false;
                }

                normalizedPrediction = $"{side} {line.Value:0.0} Sets";
                return true;
            }

            case SetHandicapBookingMarket:
            {
                var match = PredictionHandicapRegex.Match(trimmedPrediction);
                if (!match.Success)
                {
                    return false;
                }

                if (!double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var line))
                {
                    return false;
                }

                var side = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(match.Groups[1].Value.ToLowerInvariant());
                normalizedPrediction = $"{side} {line:+0.0;-0.0} Sets";
                return true;
            }

            default:
                return false;
        }
    }

    private static double? TryParseNumericLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = NumericLineRegex.Match(text);
        return match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var line)
            ? line
            : null;
    }

    private static bool NeedsWinnerMarketHydration(SportyBetFixture fixture)
    {
        return string.IsNullOrWhiteSpace(fixture.HomeOutcomeId) ||
               string.IsNullOrWhiteSpace(fixture.AwayOutcomeId) ||
               fixture.HomeOdds is null ||
               fixture.AwayOdds is null ||
               fixture.MarketSelections.Count == 0;
    }

    private static JsonElement? SelectMatchWinnerMarket(JsonElement marketsElement, string preferredMarketId)
    {
        JsonElement? preferredById = null;
        JsonElement? bestWinnerMarket = null;

        foreach (var market in marketsElement.EnumerateArray())
        {
            if (!market.TryGetProperty("outcomes", out var outcomesElement) ||
                outcomesElement.ValueKind != JsonValueKind.Array ||
                outcomesElement.GetArrayLength() != 2)
            {
                continue;
            }

            var marketId = market.TryGetProperty("id", out var marketIdElement)
                ? marketIdElement.GetString() ?? string.Empty
                : string.Empty;
            if (string.Equals(marketId, preferredMarketId, StringComparison.Ordinal))
            {
                preferredById = market;
            }

            var description = GetMarketText(market, "desc");
            var name = GetMarketText(market, "name");
            var title = GetMarketText(market, "title");
            var guide = GetMarketText(market, "marketGuide");
            var normalizedText = $"{description} {name} {title} {guide}".Trim();

            if (guide.Contains("win the match", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(description, "winner", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "winner", StringComparison.OrdinalIgnoreCase) ||
                (normalizedText.Contains("winner", StringComparison.OrdinalIgnoreCase) &&
                 !normalizedText.Contains("set", StringComparison.OrdinalIgnoreCase)))
            {
                bestWinnerMarket ??= market;
            }
        }

        return bestWinnerMarket ?? preferredById;
    }

    private static string GetMarketText(JsonElement market, string propertyName)
    {
        return market.TryGetProperty(propertyName, out var property)
            ? property.GetString() ?? string.Empty
            : string.Empty;
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
        {
            return tournament;
        }

        if (string.IsNullOrWhiteSpace(tournament))
        {
            return country;
        }

        return $"{country} - {tournament}";
    }

    private static double? TryParseProbability(JsonElement outcomeElement)
    {
        if (!outcomeElement.TryGetProperty("probability", out var probabilityElement))
        {
            return null;
        }

        if (probabilityElement.ValueKind == JsonValueKind.Number && probabilityElement.TryGetDouble(out var numericProbability))
        {
            return numericProbability;
        }

        if (probabilityElement.ValueKind == JsonValueKind.String &&
            double.TryParse(probabilityElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var stringProbability))
        {
            return stringProbability;
        }

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

public record SportyBetFixture
{
    public string EventId { get; init; } = string.Empty;
    public string League { get; init; } = string.Empty;
    public string HomeTeam { get; init; } = string.Empty;
    public string AwayTeam { get; init; } = string.Empty;
    public DateTime? MatchTimeUtc { get; init; }
    public string WinnerMarketId { get; init; } = "1";
    public string HomeOutcomeId { get; init; } = string.Empty;
    public string AwayOutcomeId { get; init; } = string.Empty;
    public double? HomeProbability { get; init; }
    public double? HomeOdds { get; init; }
    public double? AwayProbability { get; init; }
    public double? AwayOdds { get; init; }
    public List<SportyBetFixtureSelection> MarketSelections { get; init; } = [];
}

public record SportyBetOutcome
{
    public string EventId { get; init; } = string.Empty;
    public string OutcomeId { get; init; } = string.Empty;
    public string MarketId { get; init; } = "1";
    public string? Specifier { get; init; }
    public string HomeTeam { get; init; } = string.Empty;
    public string AwayTeam { get; init; } = string.Empty;
}

public sealed record SportyBetFixtureSelection(
    string Market,
    string Prediction,
    string MarketId,
    string? Specifier,
    string OutcomeId,
    string Descriptor,
    double? Probability,
    double? DecimalOdds);

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

internal sealed record ParsedOutcome(
    string OutcomeId,
    string Descriptor,
    double? Probability,
    double? DecimalOdds);

internal enum BookingSelectionMatchStatus
{
    Matched,
    OutsideTodayWindow,
    NoFixtureFound,
    AmbiguousFixture,
    MarketUnavailable
}

internal sealed record RequestedSportyBetOutcome(
    string Market,
    string Prediction,
    LegacyRequestedSportyBetOutcome? LegacyOutcome);

internal enum LegacyRequestedSportyBetOutcome
{
    HomeWin,
    AwayWin
}
