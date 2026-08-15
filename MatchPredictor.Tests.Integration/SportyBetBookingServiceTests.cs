using System.Net;
using System.Text;
using System.Text.Json;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Services;
using MatchPredictor.Infrastructure.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class SportyBetBookingServiceTests
{
    [Fact]
    public async Task BookGamesAsync_FetchesPastOldBookingCeiling_WhenSelectionAppearsOnPageFive()
    {
        await using var context = CreateContext();
        var cache = CreateCache();
        var todayLocalDate = DateTimeProvider.GetLocalDate();
        var kickoffUtc = DateTimeProvider.ConvertLocalToUtc(todayLocalDate.ToDateTime(new TimeOnly(18, 0), DateTimeKind.Unspecified));
        var handler = new SportyBetTestHandler(
            upcomingPages: new Dictionary<int, string>
            {
                [1] = BuildUpcomingResponse(),
                [2] = BuildUpcomingResponse(),
                [3] = BuildUpcomingResponse(),
                [4] = BuildUpcomingResponse(),
                [5] = BuildUpcomingResponse(
                    new SportyFixtureSpec("evt-5", "Rivers United", "Plateau United", "Nigeria - Premier League", kickoffUtc))
            },
            bookingResponse: BuildBookingShareResponse("SHARE5"));
        var service = CreateService(context, cache, handler);

        var result = await service.BookGamesAsync(
        [
            new BookingSelection
            {
                HomeTeam = "Rivers United",
                AwayTeam = "Plateau United",
                League = "Nigeria - Premier League",
                Market = "1X2",
                Prediction = "Home Win",
                MatchDateTimeUtc = kickoffUtc
            }
        ]);

        Assert.True(result.Success);
        Assert.Equal(1, result.BookedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Contains(5, handler.RequestedPages);
        Assert.Contains("evt-5", handler.SharedEventIds);
    }

    [Fact]
    public async Task BookGamesAsync_PrefersClosestKickoff_WhenTeamsRepeatOnSameDay()
    {
        await using var context = CreateContext();
        var cache = CreateCache();
        var todayLocalDate = DateTimeProvider.GetLocalDate();
        var earlyKickoffUtc = DateTimeProvider.ConvertLocalToUtc(todayLocalDate.ToDateTime(new TimeOnly(13, 0), DateTimeKind.Unspecified));
        var lateKickoffUtc = DateTimeProvider.ConvertLocalToUtc(todayLocalDate.ToDateTime(new TimeOnly(16, 0), DateTimeKind.Unspecified));
        var handler = new SportyBetTestHandler(
            upcomingPages: new Dictionary<int, string>
            {
                [1] = BuildUpcomingResponse(
                    new SportyFixtureSpec("evt-early", "Arsenal", "Chelsea", "England - Premier League", earlyKickoffUtc),
                    new SportyFixtureSpec("evt-late", "Arsenal", "Chelsea", "England - Premier League", lateKickoffUtc))
            },
            bookingResponse: BuildBookingShareResponse("CLOSEST"));
        var service = CreateService(context, cache, handler);

        var result = await service.BookGamesAsync(
        [
            new BookingSelection
            {
                HomeTeam = "Arsenal",
                AwayTeam = "Chelsea",
                League = "England - Premier League",
                Market = "1X2",
                Prediction = "Home Win",
                MatchDateTimeUtc = lateKickoffUtc
            }
        ]);

        Assert.True(result.Success);
        Assert.Single(handler.SharedEventIds);
        Assert.Equal("evt-late", handler.SharedEventIds[0]);
    }

    [Fact]
    public async Task BookGamesAsync_UsesPredictionIdContext_WhenPayloadFieldsAreStale()
    {
        await using var context = CreateContext();
        var cache = CreateCache();
        var todayLocalDate = DateTimeProvider.GetLocalDate();
        var kickoffLocal = todayLocalDate.ToDateTime(new TimeOnly(17, 30), DateTimeKind.Unspecified);
        var kickoffUtc = DateTimeProvider.ConvertLocalToUtc(kickoffLocal);

        var prediction = new Prediction
        {
            Id = 501,
            Date = todayLocalDate.ToString("dd-MM-yyyy"),
            Time = "17:30",
            MatchLocalDate = todayLocalDate,
            MatchLocalTime = TimeOnly.FromDateTime(kickoffLocal),
            MatchDateTime = kickoffUtc,
            FixtureKey = "fixture-501",
            League = "Spain - LaLiga",
            HomeTeam = "Barcelona",
            AwayTeam = "Valencia",
            PredictionCategory = "Over2.5Goals",
            PredictedOutcome = "Over 2.5",
            PredictionRunId = Guid.NewGuid(),
            RunLabel = "test",
            RunReason = "test"
        };
        context.Predictions.Add(prediction);
        await context.SaveChangesAsync();

        var handler = new SportyBetTestHandler(
            upcomingPages: new Dictionary<int, string>
            {
                [1] = BuildUpcomingResponse(
                    new SportyFixtureSpec("evt-barca", "Barcelona", "Valencia", "Spain - LaLiga", kickoffUtc))
            },
            bookingResponse: BuildBookingShareResponse("PRED501"));
        var service = CreateService(context, cache, handler);

        var result = await service.BookGamesAsync(
        [
            new BookingSelection
            {
                PredictionId = 501,
                HomeTeam = "Wrong Team",
                AwayTeam = "Wrong Opponent",
                League = "Wrong League",
                Market = "1X2",
                Prediction = "Home Win",
                MatchDateTimeUtc = DateTimeProvider.ConvertLocalToUtc(kickoffLocal.AddHours(4))
            }
        ]);

        Assert.True(result.Success);
        Assert.Single(handler.SharedEventIds);
        Assert.Equal("evt-barca", handler.SharedEventIds[0]);
    }

    [Fact]
    public async Task BookGamesAsync_ReturnsPartialBookingCounts_WhenTomorrowSelectionIsSkipped()
    {
        await using var context = CreateContext();
        var cache = CreateCache();
        var todayLocalDate = DateTimeProvider.GetLocalDate();
        var todayKickoffUtc = DateTimeProvider.ConvertLocalToUtc(todayLocalDate.ToDateTime(new TimeOnly(15, 0), DateTimeKind.Unspecified));
        var tomorrowKickoffUtc = DateTimeProvider.ConvertLocalToUtc(todayLocalDate.AddDays(1).ToDateTime(new TimeOnly(15, 0), DateTimeKind.Unspecified));
        var handler = new SportyBetTestHandler(
            upcomingPages: new Dictionary<int, string>
            {
                [1] = BuildUpcomingResponse(
                    new SportyFixtureSpec("evt-today", "Kano Pillars", "Enyimba", "Nigeria - Premier League", todayKickoffUtc))
            },
            bookingResponse: BuildBookingShareResponse("PARTIAL1"));
        var service = CreateService(context, cache, handler);

        var result = await service.BookGamesAsync(
        [
            new BookingSelection
            {
                HomeTeam = "Kano Pillars",
                AwayTeam = "Enyimba",
                League = "Nigeria - Premier League",
                Market = "1X2",
                Prediction = "Home Win",
                MatchDateTimeUtc = todayKickoffUtc
            },
            new BookingSelection
            {
                HomeTeam = "Remo Stars",
                AwayTeam = "Sunshine Stars",
                League = "Nigeria - Premier League",
                Market = "1X2",
                Prediction = "Home Win",
                MatchDateTimeUtc = tomorrowKickoffUtc
            }
        ]);

        Assert.True(result.Success);
        Assert.Equal(1, result.BookedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Contains(result.Warnings, warning => warning.Contains("outside today's SportyBet card", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BookGamesAsync_SkipsAmbiguousLookalikesInsteadOfGuessing()
    {
        await using var context = CreateContext();
        var cache = CreateCache();
        var todayLocalDate = DateTimeProvider.GetLocalDate();
        var kickoffUtc = DateTimeProvider.ConvertLocalToUtc(todayLocalDate.ToDateTime(new TimeOnly(14, 0), DateTimeKind.Unspecified));
        var handler = new SportyBetTestHandler(
            upcomingPages: new Dictionary<int, string>
            {
                [1] = BuildUpcomingResponse(
                    new SportyFixtureSpec("evt-a", "Alpha United", "Beta FC", "League A", kickoffUtc),
                    new SportyFixtureSpec("evt-b", "Alpha United", "Beta FC", "League B", kickoffUtc))
            });
        var service = CreateService(context, cache, handler);

        var result = await service.BookGamesAsync(
        [
            new BookingSelection
            {
                HomeTeam = "Alpha United",
                AwayTeam = "Beta",
                League = string.Empty,
                Market = "1X2",
                Prediction = "Home Win",
                MatchDateTimeUtc = kickoffUtc
            }
        ]);

        Assert.False(result.Success);
        Assert.Equal(0, result.BookedCount);
        Assert.Contains(result.Warnings, warning => warning.Contains("ambiguous", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(handler.SharedEventIds);
    }

    [Fact]
    public async Task BookGamesAsync_ReturnsMarketUnavailableWarning_WhenFixtureLacksRequestedOutcome()
    {
        await using var context = CreateContext();
        var cache = CreateCache();
        var todayLocalDate = DateTimeProvider.GetLocalDate();
        var kickoffUtc = DateTimeProvider.ConvertLocalToUtc(todayLocalDate.ToDateTime(new TimeOnly(19, 0), DateTimeKind.Unspecified));
        var handler = new SportyBetTestHandler(
            upcomingPages: new Dictionary<int, string>
            {
                [1] = BuildUpcomingResponse(
                    new SportyFixtureSpec("evt-under", "Inter", "Milan", "Italy - Serie A", kickoffUtc, IncludeTotals: false))
            });
        var service = CreateService(context, cache, handler);

        var result = await service.BookGamesAsync(
        [
            new BookingSelection
            {
                HomeTeam = "Inter",
                AwayTeam = "Milan",
                League = "Italy - Serie A",
                Market = "Under2.5",
                Prediction = "Under 2.5",
                MatchDateTimeUtc = kickoffUtc
            }
        ]);

        Assert.False(result.Success);
        Assert.Equal(0, result.BookedCount);
        Assert.Contains(result.Warnings, warning => warning.Contains("market", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(handler.SharedEventIds);
    }


    [Fact]
    public async Task BookGamesAsync_MatchesPsgAlias_ForBttsSelection()
    {
        await using var context = CreateContext();
        var cache = CreateCache();
        var todayLocalDate = DateTimeProvider.GetLocalDate();
        var kickoffUtc = DateTimeProvider.ConvertLocalToUtc(todayLocalDate.ToDateTime(new TimeOnly(20, 0), DateTimeKind.Unspecified));
        var handler = new SportyBetTestHandler(
            upcomingPages: new Dictionary<int, string>
            {
                [1] = BuildUpcomingResponse(
                    new SportyFixtureSpec("evt-psg", "Nice", "PSG", "France - Ligue 1", kickoffUtc))
            },
            bookingResponse: BuildBookingShareResponse("BTTSPSG"));
        var service = CreateService(context, cache, handler);

        var result = await service.BookGamesAsync(
        [
            new BookingSelection
            {
                HomeTeam = "Nice",
                AwayTeam = "Paris Saint-Germain",
                League = "France - Ligue 1",
                Market = "BTTS",
                Prediction = "BTTS",
                MatchDateTimeUtc = kickoffUtc
            }
        ]);

        Assert.True(result.Success);
        Assert.Equal(1, result.BookedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Single(handler.SharedEventIds);
        Assert.Equal("evt-psg", handler.SharedEventIds[0]);
    }

    [Fact]
    public async Task BookGamesAsync_UsesNoConfidentMatchWarning_WhenOnlyLooseLookalikeExists()
    {
        await using var context = CreateContext();
        var cache = CreateCache();
        var todayLocalDate = DateTimeProvider.GetLocalDate();
        var kickoffUtc = DateTimeProvider.ConvertLocalToUtc(todayLocalDate.ToDateTime(new TimeOnly(20, 0), DateTimeKind.Unspecified));
        var handler = new SportyBetTestHandler(
            upcomingPages: new Dictionary<int, string>
            {
                [1] = BuildUpcomingResponse(
                    new SportyFixtureSpec("evt-close", "Nice", "Marseille", "France - Ligue 1", kickoffUtc))
            });
        var service = CreateService(context, cache, handler);

        var result = await service.BookGamesAsync(
        [
            new BookingSelection
            {
                HomeTeam = "Nice",
                AwayTeam = "Paris Saint-Germain",
                League = "France - Ligue 1",
                Market = "BTTS",
                Prediction = "BTTS",
                MatchDateTimeUtc = kickoffUtc
            }
        ]);

        Assert.False(result.Success);
        Assert.Equal(0, result.BookedCount);
        Assert.Contains(
            result.Warnings,
            warning => warning.Contains("no confident SportyBet fixture match found", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            result.Warnings,
            warning => warning.Contains("Closest candidate was Nice vs Marseille", StringComparison.OrdinalIgnoreCase));
        Assert.Single(result.UnresolvedSelections);
        Assert.Equal("evt-close", result.UnresolvedSelections[0].ClosestEventId);
    }

    [Fact]
    public async Task BookGamesAsync_UsesConfirmedEventId_WhenAutomaticMatchIsUncertain()
    {
        await using var context = CreateContext();
        var cache = CreateCache();
        var todayLocalDate = DateTimeProvider.GetLocalDate();
        var kickoffUtc = DateTimeProvider.ConvertLocalToUtc(todayLocalDate.ToDateTime(new TimeOnly(20, 0), DateTimeKind.Unspecified));
        var handler = new SportyBetTestHandler(
            upcomingPages: new Dictionary<int, string>
            {
                [1] = BuildUpcomingResponse(
                    new SportyFixtureSpec("evt-close", "Nice", "Marseille", "France - Ligue 1", kickoffUtc))
            },
            bookingResponse: BuildBookingShareResponse("CONFIRMED"));
        var service = CreateService(context, cache, handler);

        var result = await service.BookGamesAsync(
        [
            new BookingSelection
            {
                HomeTeam = "Nice",
                AwayTeam = "Paris Saint-Germain",
                League = "France - Ligue 1",
                Market = "BTTS",
                Prediction = "BTTS",
                MatchDateTimeUtc = kickoffUtc,
                ConfirmedSportyBetEventId = "evt-close"
            }
        ]);

        Assert.True(result.Success);
        Assert.Equal(1, result.BookedCount);
        Assert.Empty(result.UnresolvedSelections);
        Assert.Equal(["evt-close"], handler.SharedEventIds);
    }

    [Fact]
    public async Task GetTodaySourceMarketFixturesAsync_ParsesOver25Odds_WhenSpecifierIsTwoFifty()
    {
        await using var context = CreateContext();
        var cache = CreateCache();
        var todayLocalDate = DateTimeProvider.GetLocalDate();
        var kickoffUtc = DateTimeProvider.ConvertLocalToUtc(todayLocalDate.ToDateTime(new TimeOnly(18, 0), DateTimeKind.Unspecified));
        var handler = new SportyBetTestHandler(
            upcomingPages: new Dictionary<int, string>
            {
                [1] = BuildUpcomingResponse(
                    new SportyFixtureSpec(
                        "evt-ou",
                        "Over Home",
                        "Over Away",
                        "England - Premier League",
                        kickoffUtc,
                        TotalsSpecifier: "total=2.50"))
            });
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SportyBet:BaseUrl"] = "https://sporty.test",
                ["SportyBet:SoccerSportId"] = "sr:sport:1",
                ["SportyBet:Market1X2"] = "1",
                ["SportyBet:PricingPageSize"] = "100",
                ["SportyBet:PricingMaxPages"] = "1",
                ["SportyBet:PricingTimeoutSeconds"] = "30"
            })
            .Build();
        var service = new SportyBetBookingService(
            configuration,
            NullLogger<SportyBetBookingService>.Instance,
            new StubHttpClientFactory(handler),
            cache,
            context);

        var fixtures = await service.GetTodaySourceMarketFixturesAsync();

        var fixture = Assert.Single(fixtures);
        Assert.Equal("evt-ou", fixture.EventId);
        Assert.Equal(1.90, fixture.Over25Odds);
        Assert.Equal(2.05, fixture.Under25Odds);
    }

    private static SportyBetBookingService CreateService(
        ApplicationDbContext context,
        IDistributedCache cache,
        SportyBetTestHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SportyBet:BaseUrl"] = "https://sporty.test",
                ["SportyBet:SoccerSportId"] = "sr:sport:1",
                ["SportyBet:Market1X2"] = "1",
                ["SportyBet:BookingPageSize"] = "100",
                ["SportyBet:BookingMaxPages"] = "10",
                ["SportyBet:BookingTimeoutSeconds"] = "60",
                ["SportyBet:PricingPageSize"] = "100",
                ["SportyBet:PricingMaxPages"] = "2",
                ["SportyBet:PricingTimeoutSeconds"] = "30"
            })
            .Build();

        return new SportyBetBookingService(
            configuration,
            NullLogger<SportyBetBookingService>.Instance,
            new StubHttpClientFactory(handler),
            cache,
            context);
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options);
    }

    private static IDistributedCache CreateCache()
    {
        return new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
    }

    private static string BuildUpcomingResponse(params SportyFixtureSpec[] fixtures)
    {
        var events = fixtures.Select(fixture =>
        {
            var (country, tournament) = SplitLeague(fixture.League);

            return new
            {
                eventId = fixture.EventId,
                homeTeamName = fixture.HomeTeam,
                awayTeamName = fixture.AwayTeam,
                estimateStartTime = fixture.MatchTimeUtc.HasValue
                    ? new DateTimeOffset(fixture.MatchTimeUtc.Value).ToUnixTimeMilliseconds()
                    : (long?)null,
                sport = new
                {
                    category = new
                    {
                        name = country,
                        tournament = new
                        {
                            name = tournament
                        }
                    }
                },
                markets = BuildMarkets(fixture)
            };
        }).ToArray();

        return JsonSerializer.Serialize(new
        {
            data = new
            {
                tournaments = new[]
                {
                    new
                    {
                        events
                    }
                }
            }
        });
    }

    private static object[] BuildMarkets(SportyFixtureSpec fixture)
    {
        var markets = new List<object>();

        if (fixture.Include1X2)
        {
            markets.Add(new
            {
                id = "1",
                outcomes = new[]
                {
                    new { id = "1", desc = "Home", probability = "0.40", odds = "2.50" },
                    new { id = "2", desc = "Draw", probability = "0.30", odds = "3.10" },
                    new { id = "3", desc = "Away", probability = "0.30", odds = "3.00" }
                }
            });
        }

        if (fixture.IncludeTotals)
        {
            markets.Add(new
            {
                id = "18",
                specifier = fixture.TotalsSpecifier,
                outcomes = new[]
                {
                    new { id = "12", desc = "Over 2.5", probability = "0.52", odds = "1.90" },
                    new { id = "13", desc = "Under 2.5", probability = "0.48", odds = "2.05" }
                }
            });
        }

        if (fixture.IncludeBtts)
        {
            markets.Add(new
            {
                id = "29",
                outcomes = new[]
                {
                    new { id = "74", desc = "Yes", probability = "0.51", odds = "1.95" },
                    new { id = "76", desc = "No", probability = "0.49", odds = "2.00" }
                }
            });
        }

        return markets.ToArray();
    }

    private static string BuildBookingShareResponse(string shareCode)
    {
        return JsonSerializer.Serialize(new
        {
            data = new
            {
                shareCode,
                shareURL = $"https://sporty.test/share/{shareCode}"
            }
        });
    }

    private static (string Country, string Tournament) SplitLeague(string league)
    {
        var parts = league.Split(" - ", 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 ? (parts[0], parts[1]) : (league, "League");
    }

    private sealed record SportyFixtureSpec(
        string EventId,
        string HomeTeam,
        string AwayTeam,
        string League,
        DateTime? MatchTimeUtc,
        bool Include1X2 = true,
        bool IncludeTotals = true,
        bool IncludeBtts = true,
        string TotalsSpecifier = "total=2.5");

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class SportyBetTestHandler : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<int, string> _upcomingPages;
        private readonly string? _bookingResponse;

        public SportyBetTestHandler(
            IReadOnlyDictionary<int, string>? upcomingPages = null,
            string? bookingResponse = null)
        {
            _upcomingPages = upcomingPages ?? new Dictionary<int, string>();
            _bookingResponse = bookingResponse;
        }

        public List<int> RequestedPages { get; } = [];
        public List<string> SharedEventIds { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Method == HttpMethod.Get && path.Contains("pcUpcomingEvents", StringComparison.Ordinal))
            {
                var pageNum = ParseIntQueryValue(request.RequestUri?.Query, "pageNum");
                RequestedPages.Add(pageNum);
                var body = _upcomingPages.TryGetValue(pageNum, out var response)
                    ? response
                    : BuildUpcomingResponse();

                return CreateJsonResponse(body);
            }

            if (request.Method == HttpMethod.Post && path.Contains("orders/share", StringComparison.Ordinal))
            {
                var payload = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(payload);
                SharedEventIds.AddRange(document.RootElement
                    .GetProperty("selections")
                    .EnumerateArray()
                    .Select(selection => selection.GetProperty("eventId").GetString() ?? string.Empty));

                return CreateJsonResponse(_bookingResponse ?? BuildBookingShareResponse("DEFAULT"));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }

        private static HttpResponseMessage CreateJsonResponse(string body)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }

        private static int ParseIntQueryValue(string? query, string key)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return 0;
            }

            var trimmed = query.TrimStart('?');
            foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 &&
                    string.Equals(parts[0], key, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(Uri.UnescapeDataString(parts[1]), out var value))
                {
                    return value;
                }
            }

            return 0;
        }
    }
}
