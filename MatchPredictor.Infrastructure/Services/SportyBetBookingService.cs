using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using System.Globalization;

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
    private readonly IConfiguration _configuration;
    private readonly ILogger<SportyBetBookingService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDistributedCache _cache;

    // Values read from appsettings SportyBet section

    public SportyBetBookingService(
        IConfiguration configuration,
        ILogger<SportyBetBookingService> logger,
        IHttpClientFactory httpClientFactory,
        IDistributedCache cache)
    {
        _configuration = configuration;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _cache = cache;
    }

    public async Task<BookingResult> BookGamesAsync(List<BookingSelection> selections)
    {
        if (selections.Count == 0)
            return new BookingResult { Success = false, Message = "No games selected." };

        var baseUrl = _configuration["SportyBet:BaseUrl"] ?? "https://www.sportybet.com";
        var soccerSportId = _configuration["SportyBet:SoccerSportId"] ?? "sr:sport:1";
        var market1X2 = _configuration["SportyBet:Market1X2"] ?? "1";

        try
        {
            _logger.LogInformation("Searching SportyBet API for {Count} selections...", selections.Count);

            // Step 1: Fetch today's fixtures from SportyBet to get outcome IDs
            var fixtureMap = await FetchTodayFixturesAsync(baseUrl, soccerSportId, market1X2, CancellationToken.None);
            if (fixtureMap.Count == 0)
            {
                _logger.LogWarning("Could not fetch fixtures from SportyBet API.");
                return new BookingResult { Success = false, Message = "Could not fetch today's fixtures from SportyBet." };
            }

            _logger.LogInformation("Fetched {Count} fixtures from SportyBet.", fixtureMap.Count);

            // Step 2: Match each selection to a SportyBet fixture and get the right outcome
            var selectedOutcomes = new List<SportyBetOutcome>();

            foreach (var sel in selections)
            {
                var matched = FindBestMatch(fixtureMap, sel);
                if (matched != null)
                {
                    selectedOutcomes.Add(matched);
                    _logger.LogInformation("Matched: {Home} vs {Away} → outcomeId={OutcomeId}",
                        sel.HomeTeam, sel.AwayTeam, matched.OutcomeId);
                }
                else
                {
                    _logger.LogWarning("No SportyBet match found for: {Home} vs {Away}", sel.HomeTeam, sel.AwayTeam);
                }
            }

            if (selectedOutcomes.Count == 0)
            {
                return new BookingResult
                {
                    Success = false,
                    Message = "None of the selected matches were found on SportyBet today."
                };
            }

            // Step 3: Create booking code via API
            var (bookingCode, bookingUrl) = await CreateBookingCodeAsync(selectedOutcomes, baseUrl);

            if (!string.IsNullOrEmpty(bookingCode))
            {
                return new BookingResult
                {
                    Success = true,
                    BookingCode = bookingCode,
                    BookingUrl = bookingUrl ?? "",
                    Message = $"Booked {selectedOutcomes.Count}/{selections.Count} games."
                };
            }

            return new BookingResult
            {
                Success = false,
                Message = $"Found {selectedOutcomes.Count} matches but could not generate booking code."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during SportyBet booking via API.");
            return new BookingResult { Success = false, Message = $"Booking error: {ex.Message}" };
        }
    }

    public async Task<IReadOnlyList<SourceMarketFixture>> GetTodaySourceMarketFixturesAsync(CancellationToken ct = default)
    {
        var baseUrl = _configuration["SportyBet:BaseUrl"] ?? "https://www.sportybet.com";
        var soccerSportId = _configuration["SportyBet:SoccerSportId"] ?? "sr:sport:1";
        var market1X2 = _configuration["SportyBet:Market1X2"] ?? "1";

        var fixtures = await FetchTodayFixturesAsync(baseUrl, soccerSportId, market1X2, ct);
        return fixtures.Select(fixture => new SourceMarketFixture
        {
            EventId = fixture.EventId,
            League = fixture.League,
            HomeTeam = fixture.HomeTeam,
            AwayTeam = fixture.AwayTeam,
            MatchTimeUtc = fixture.MatchTimeUtc,
            HomeWinProbability = fixture.HomeProbability,
            DrawProbability = fixture.DrawProbability,
            AwayWinProbability = fixture.AwayProbability,
            Over25Probability = fixture.Over25Probability,
            BttsYesProbability = fixture.BttsYesProbability,
            BttsNoProbability = fixture.BttsNoProbability
        }).ToList();
    }

    /// <summary>
    /// Fetches today's football fixtures from SportyBet and returns a flat list indexed by fixture.
    /// </summary>
    private async Task<List<SportyBetFixture>> FetchTodayFixturesAsync(string baseUrl, string soccerSportId, string market1X2, CancellationToken ct)
    {
        var cacheKey = $"sportybet_fixtures_{DateTime.UtcNow:yyyyMMdd}";
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
            _logger.LogInformation("Returning SportyBet fixtures from Redis cache.");
            return JsonSerializer.Deserialize<List<SportyBetFixture>>(cachedData) ?? new List<SportyBetFixture>();
        }

        var fixtures = new List<SportyBetFixture>();
        var client = CreateHttpClient();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Paginate — SportyBet uses pageNum (not pageIndex), todayGames=true, timeline=2.9
        for (int page = 1; page <= 10; page++)
        {
            try
            {
                // Request multiple markets: 1 (1X2), 18 (Over/Under), 29 (GG/NG / BTTS)
                var url = $"{baseUrl}/api/ng/factsCenter/pcUpcomingEvents" +
                           $"?sportId={Uri.EscapeDataString(soccerSportId)}" +
                           $"&marketId={Uri.EscapeDataString(market1X2)},18,29" +
                           $"&pageSize=100&pageNum={page}" +
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
                            var bttsOutcomeId = "";
                            var over25OutcomeId = "";
                            double? homeProbability = null;
                            double? drawProbability = null;
                            double? awayProbability = null;
                            double? bttsYesProbability = null;
                            double? bttsNoProbability = null;
                            double? over25Probability = null;

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
                                                
                                                if (oid == "1" || desc.Contains("Home", StringComparison.OrdinalIgnoreCase))
                                                {
                                                    homeOutcomeId = oid;
                                                    homeProbability = probability;
                                                }
                                                else if (oid == "2" || desc.Contains("Draw", StringComparison.OrdinalIgnoreCase))
                                                {
                                                    drawOutcomeId = oid;
                                                    drawProbability = probability;
                                                }
                                                else if (oid == "3" || desc.Contains("Away", StringComparison.OrdinalIgnoreCase))
                                                {
                                                    awayOutcomeId = oid;
                                                    awayProbability = probability;
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
                                                    if (oid == "12" || desc.Contains("Over", StringComparison.OrdinalIgnoreCase))
                                                    {
                                                        over25OutcomeId = oid;
                                                        over25Probability = probability;
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
                                                if (oid == "74" || desc.Equals("Yes", StringComparison.OrdinalIgnoreCase))
                                                {
                                                    bttsOutcomeId = oid;
                                                    bttsYesProbability = probability;
                                                }
                                                else if (oid == "76" || desc.Equals("No", StringComparison.OrdinalIgnoreCase))
                                                {
                                                    bttsNoProbability = probability;
                                                }
                                            }
                                        }
                                    }
                                }
                            }

                            fixtures.Add(new SportyBetFixture
                            {
                                EventId = eventId,
                                League = league,
                                HomeTeam = homeTeam,
                                AwayTeam = awayTeam,
                                MatchTimeUtc = kickoffTimeUtc,
                                HomeOutcomeId = homeOutcomeId,
                                DrawOutcomeId = drawOutcomeId,
                                AwayOutcomeId = awayOutcomeId,
                                BttsYesOutcomeId = bttsOutcomeId,
                                Over25OutcomeId = over25OutcomeId,
                                HomeProbability = homeProbability,
                                DrawProbability = drawProbability,
                                AwayProbability = awayProbability,
                                Over25Probability = over25Probability,
                                BttsYesProbability = bttsYesProbability,
                                BttsNoProbability = bttsNoProbability
                            });
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Skipping fixture due to parse error");
                        }
                    }
                }

                _logger.LogInformation("SportyBet page {Page}: {Tournaments} tournaments, {Fixtures} fixtures parsed.",
                    page, tournamentCount, fixtures.Count);

                // If no tournaments, we're done
                if (tournamentCount == 0) break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error fetching SportyBet fixtures page {Page}", page);
                break;
            }
        }

        if (fixtures.Count > 0)
        {
            try
            {
                var serialized = JsonSerializer.Serialize(fixtures);
                var cacheOptions = new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15) // Cache for 15 mins
                };
                await _cache.SetStringAsync(cacheKey, serialized, cacheOptions, ct);
                _logger.LogInformation("Cached {Count} SportyBet fixtures in Redis.", fixtures.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write to Redis cache. Continuing without caching.");
            }
        }

        return fixtures;
    }

    /// <summary>
    /// Finds the best-matching SportyBet fixture for a selection using fuzzy team name matching.
    /// </summary>
    private static SportyBetOutcome? FindBestMatch(List<SportyBetFixture> fixtures, BookingSelection selection)
    {
        var homeNorm = NormalizeTeamName(selection.HomeTeam);
        var awayNorm = NormalizeTeamName(selection.AwayTeam);

        SportyBetFixture? best = null;
        var bestScore = 0;

        foreach (var fix in fixtures)
        {
            var fixtureHomeNorm = NormalizeTeamName(fix.HomeTeam);
            var fixtureAwayNorm = NormalizeTeamName(fix.AwayTeam);

            var score = 0;
            if (fixtureHomeNorm.Contains(homeNorm) || homeNorm.Contains(fixtureHomeNorm)) score += 2;
            if (fixtureAwayNorm.Contains(awayNorm) || awayNorm.Contains(fixtureAwayNorm)) score += 2;
            if (fixtureHomeNorm.Contains(homeNorm[..Math.Min(4, homeNorm.Length)])) score += 1;
            if (fixtureAwayNorm.Contains(awayNorm[..Math.Min(4, awayNorm.Length)])) score += 1;

            if (score > bestScore)
            {
                bestScore = score;
                best = fix;
            }
        }

        if (best == null || bestScore < 2) return null;

        // Resolve which outcome ID to use based on prediction
        var prediction = selection.Prediction?.ToLowerInvariant() ?? "";
        string outcomeId;
        string marketId = "1"; // Default to 1X2 market
        string? specifier = null;

        if (prediction.Contains("btts") || prediction.Contains("both teams"))
        {
            outcomeId = best.BttsYesOutcomeId;
            marketId = "29";
        }
        else if (prediction.Contains("over 2.5") || prediction.Contains("over2.5"))
        {
            outcomeId = best.Over25OutcomeId;
            marketId = "18";
            specifier = "total=2.5";
        }
        else if (prediction.Contains("draw") || prediction == "x")
            outcomeId = best.DrawOutcomeId;
        else if (prediction.Contains("away") || prediction.Contains("2"))
            outcomeId = best.AwayOutcomeId;
        else // home win
            outcomeId = best.HomeOutcomeId;

        if (string.IsNullOrEmpty(outcomeId)) return null;

        return new SportyBetOutcome
        {
            EventId = best.EventId,
            OutcomeId = outcomeId,
            MarketId = marketId,
            Specifier = specifier,
            HomeTeam = best.HomeTeam,
            AwayTeam = best.AwayTeam
        };
    }

    /// <summary>
    /// Calls POST /api/ng/orders/share to generate a booking code.
    /// Payload format reverse-engineered from SportyBet network traffic.
    /// </summary>
    private async Task<(string? Code, string? Url)> CreateBookingCodeAsync(List<SportyBetOutcome> outcomes, string baseUrl)
    {
        var client = CreateHttpClient();

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

    private HttpClient CreateHttpClient()
    {
        var client = _httpClientFactory.CreateClient("SportyBet");
        var baseUrl = _configuration["SportyBet:BaseUrl"] ?? "https://www.sportybet.com";
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", $"{baseUrl}/ng/");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", baseUrl);
        client.DefaultRequestHeaders.TryAddWithoutValidation("clientid", "web");
        client.DefaultRequestHeaders.TryAddWithoutValidation("platform", "web");
        client.DefaultRequestHeaders.TryAddWithoutValidation("operid", "2");
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    private static string NormalizeTeamName(string name)
    {
        return name.ToLowerInvariant()
            .Replace("fc", "").Replace("cf", "").Replace("afc", "").Replace("sc", "")
            .Replace("united", "utd").Replace("  ", " ").Trim();
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
    public string Over25OutcomeId { get; init; } = "";
    public double? HomeProbability { get; init; }
    public double? DrawProbability { get; init; }
    public double? AwayProbability { get; init; }
    public double? Over25Probability { get; init; }
    public double? BttsYesProbability { get; init; }
    public double? BttsNoProbability { get; init; }
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
