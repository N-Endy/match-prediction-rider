using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

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
public class SportyBetBookingService : ISportyBetBookingService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<SportyBetBookingService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    // Values read from appsettings SportyBet section

    public SportyBetBookingService(
        IConfiguration configuration,
        ILogger<SportyBetBookingService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _configuration = configuration;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
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
            var fixtureMap = await FetchTodayFixturesAsync(baseUrl, soccerSportId, market1X2);
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

    /// <summary>
    /// Fetches today's football fixtures from SportyBet and returns a flat list indexed by fixture.
    /// </summary>
    private async Task<List<SportyBetFixture>> FetchTodayFixturesAsync(string baseUrl, string soccerSportId, string market1X2)
    {
        var fixtures = new List<SportyBetFixture>();
        var client = CreateHttpClient();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Paginate — SportyBet uses pageNum (not pageIndex), todayGames=true, timeline=2.9
        for (int page = 1; page <= 10; page++)
        {
            try
            {
                // Correct URL from reverse-engineered network traffic
                var url = $"{baseUrl}/api/ng/factsCenter/pcUpcomingEvents" +
                           $"?sportId={Uri.EscapeDataString(soccerSportId)}" +
                           $"&marketId={Uri.EscapeDataString(market1X2)}" +
                           $"&pageSize=100&pageNum={page}" +
                           $"&todayGames=true&timeline=2.9&_t={timestamp}";

                _logger.LogInformation("SportyBet API GET: {Url}", url);

                var response = await client.GetAsync(url);
                var responseBody = await response.Content.ReadAsStringAsync();

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

                            // Extract 1X2 market outcomes
                            var homeOutcomeId = "";
                            var drawOutcomeId = "";
                            var awayOutcomeId = "";
                            var bttsOutcomeId = "";
                            var over25OutcomeId = "";

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
                                                
                                                if (oid == "1" || desc.Contains("Home", StringComparison.OrdinalIgnoreCase))
                                                    homeOutcomeId = oid;
                                                else if (oid == "2" || desc.Contains("Draw", StringComparison.OrdinalIgnoreCase))
                                                    drawOutcomeId = oid;
                                                else if (oid == "3" || desc.Contains("Away", StringComparison.OrdinalIgnoreCase))
                                                    awayOutcomeId = oid;
                                            }
                                        }
                                    }
                                }
                            }

                            fixtures.Add(new SportyBetFixture
                            {
                                EventId = eventId,
                                HomeTeam = homeTeam,
                                AwayTeam = awayTeam,
                                HomeOutcomeId = homeOutcomeId,
                                DrawOutcomeId = drawOutcomeId,
                                AwayOutcomeId = awayOutcomeId,
                                BttsYesOutcomeId = bttsOutcomeId,
                                Over25OutcomeId = over25OutcomeId
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

        if (prediction.Contains("btts") || prediction.Contains("both teams"))
        {
            outcomeId = !string.IsNullOrEmpty(best.BttsYesOutcomeId) ? best.BttsYesOutcomeId : best.HomeOutcomeId;
        }
        else if (prediction.Contains("over 2.5") || prediction.Contains("over2.5"))
        {
            outcomeId = !string.IsNullOrEmpty(best.Over25OutcomeId) ? best.Over25OutcomeId : best.HomeOutcomeId;
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
            specifier = (string?)null,
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
}

// ── Internal Models ──

public record SportyBetFixture
{
    public string EventId { get; init; } = "";
    public string HomeTeam { get; init; } = "";
    public string AwayTeam { get; init; } = "";
    public string HomeOutcomeId { get; init; } = "";
    public string DrawOutcomeId { get; init; } = "";
    public string AwayOutcomeId { get; init; } = "";
    public string BttsYesOutcomeId { get; init; } = "";
    public string Over25OutcomeId { get; init; } = "";
}

public record SportyBetOutcome
{
    public string EventId { get; init; } = "";
    public string OutcomeId { get; init; } = "";
    public string MarketId { get; init; } = "1";
    public string HomeTeam { get; init; } = "";
    public string AwayTeam { get; init; } = "";
}
