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
    public async Task BookGamesAsync_HydratesTennisWinnerMarketFromEventDetail_WhenUpcomingFeedOmitsMarkets()
    {
        await using var context = CreateContext();
        var cache = CreateCache();
        var todayLocalDate = DateTimeProvider.GetLocalDate();
        var kickoffUtc = DateTimeProvider.ConvertLocalToUtc(todayLocalDate.ToDateTime(new TimeOnly(0, 40), DateTimeKind.Unspecified));
        var handler = new SportyBetTestHandler(
            upcomingPages: new Dictionary<int, string>
            {
                [1] = BuildUpcomingResponse(
                    new SportyFixtureSpec("evt-tennis", "Mmoh, Michael", "Zink, Tyler", "Challenger - Morelos", kickoffUtc, Include1X2: false))
            },
            eventDetails: new Dictionary<string, string>
            {
                ["evt-tennis"] = BuildTennisEventDetailResponse(
                    "evt-tennis",
                    "Mmoh, Michael",
                    "Zink, Tyler",
                    "Challenger - Morelos",
                    kickoffUtc,
                    winnerMarketId: "186",
                    homeOutcomeId: "4",
                    awayOutcomeId: "5")
            },
            bookingResponse: BuildBookingShareResponse("TENNIS186"));
        var service = CreateService(context, cache, handler);

        var result = await service.BookGamesAsync(
        [
            new BookingSelection
            {
                HomeTeam = "Michael Mmoh",
                AwayTeam = "Tyler Zink",
                League = "Challenger - Morelos",
                Market = "MatchWinner",
                Prediction = "Home Win",
                MatchDateTimeUtc = kickoffUtc
            }
        ]);

        Assert.True(result.Success);
        Assert.Equal(1, result.BookedCount);
        Assert.Contains("evt-tennis", handler.SharedEventIds);
        Assert.Contains("186", handler.SharedMarketIds);
        Assert.Contains("4", handler.SharedOutcomeIds);
        Assert.Contains("evt-tennis", handler.RequestedEventDetails);
    }

    [Fact]
    public async Task BookGamesAsync_BooksTotalSetsSelection_FromTennisEventDetail()
    {
        await using var context = CreateContext();
        var cache = CreateCache();
        var todayLocalDate = DateTimeProvider.GetLocalDate();
        var kickoffUtc = DateTimeProvider.ConvertLocalToUtc(todayLocalDate.ToDateTime(new TimeOnly(13, 10), DateTimeKind.Unspecified));
        var handler = new SportyBetTestHandler(
            upcomingPages: new Dictionary<int, string>
            {
                [1] = BuildUpcomingResponse(
                    new SportyFixtureSpec("evt-sets", "Hyeon Chung", "Yusuke Takahashi", "ATP - Busan", kickoffUtc, Include1X2: false))
            },
            eventDetails: new Dictionary<string, string>
            {
                ["evt-sets"] = BuildTennisEventDetailResponse(
                    "evt-sets",
                    "Hyeon Chung",
                    "Yusuke Takahashi",
                    "ATP - Busan",
                    kickoffUtc,
                    winnerMarketId: "186",
                    homeOutcomeId: "4",
                    awayOutcomeId: "5",
                    includeTotalSets: true)
            },
            bookingResponse: BuildBookingShareResponse("SETS314"));
        var service = CreateService(context, cache, handler);

        var result = await service.BookGamesAsync(
        [
            new BookingSelection
            {
                HomeTeam = "Hyeon Chung",
                AwayTeam = "Yusuke Takahashi",
                League = "ATP - Busan",
                Market = "OverUnderSets",
                Prediction = "Under 2.5 Sets",
                MatchDateTimeUtc = kickoffUtc
            }
        ]);

        Assert.True(result.Success);
        Assert.Equal(1, result.BookedCount);
        Assert.Contains("314", handler.SharedMarketIds);
        Assert.Contains("13", handler.SharedOutcomeIds);
        Assert.Contains("total=2.5", handler.SharedSpecifiers);
    }

    [Fact]
    public async Task BookGamesAsync_BooksSetHandicapSelection_FromTennisEventDetail()
    {
        await using var context = CreateContext();
        var cache = CreateCache();
        var todayLocalDate = DateTimeProvider.GetLocalDate();
        var kickoffUtc = DateTimeProvider.ConvertLocalToUtc(todayLocalDate.ToDateTime(new TimeOnly(15, 20), DateTimeKind.Unspecified));
        var handler = new SportyBetTestHandler(
            upcomingPages: new Dictionary<int, string>
            {
                [1] = BuildUpcomingResponse(
                    new SportyFixtureSpec("evt-hcp", "Jakub Paul", "Laurent Lokoli", "Challenger - Kigali", kickoffUtc, Include1X2: false))
            },
            eventDetails: new Dictionary<string, string>
            {
                ["evt-hcp"] = BuildTennisEventDetailResponse(
                    "evt-hcp",
                    "Jakub Paul",
                    "Laurent Lokoli",
                    "Challenger - Kigali",
                    kickoffUtc,
                    winnerMarketId: "186",
                    homeOutcomeId: "4",
                    awayOutcomeId: "5",
                    includeSetHandicap: true)
            },
            bookingResponse: BuildBookingShareResponse("HAND188"));
        var service = CreateService(context, cache, handler);

        var result = await service.BookGamesAsync(
        [
            new BookingSelection
            {
                HomeTeam = "Jakub Paul",
                AwayTeam = "Laurent Lokoli",
                League = "Challenger - Kigali",
                Market = "SetHandicap",
                Prediction = "Away +1.5 Sets",
                MatchDateTimeUtc = kickoffUtc
            }
        ]);

        Assert.True(result.Success);
        Assert.Equal(1, result.BookedCount);
        Assert.Contains("188", handler.SharedMarketIds);
        Assert.Contains("1715", handler.SharedOutcomeIds);
        Assert.Contains("hcp=-1.5", handler.SharedSpecifiers);
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
                ["SportyBet:TennisSportId"] = "sr:sport:5",
                ["SportyBet:MatchWinnerMarketId"] = "1",
                ["SportyBet:BookingPageSize"] = "100",
                ["SportyBet:BookingMaxPages"] = "10",
                ["SportyBet:BookingTimeoutSeconds"] = "60"
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

    private static string BuildTennisEventDetailResponse(
        string eventId,
        string homeTeam,
        string awayTeam,
        string league,
        DateTime? kickoffUtc,
        string winnerMarketId,
        string homeOutcomeId,
        string awayOutcomeId,
        bool includeTotalSets = false,
        bool includeSetHandicap = false)
    {
        var (country, tournament) = SplitLeague(league);

        var markets = new List<object>
        {
            new
            {
                id = winnerMarketId,
                product = 3,
                desc = "Winner",
                name = "Winner",
                marketGuide = "Who will win the match.",
                outcomes = new[]
                {
                    new { id = homeOutcomeId, desc = "Home", probability = "0.75", odds = "1.25" },
                    new { id = awayOutcomeId, desc = "Away", probability = "0.25", odds = "4.00" }
                }
            }
        };

        if (includeTotalSets)
        {
            markets.Add(new
            {
                id = "314",
                product = 3,
                specifier = "total=2.5",
                desc = "Total sets",
                name = "Total sets",
                marketGuide = "Predict how many sets will be played in the match.",
                outcomes = new[]
                {
                    new { id = "12", desc = "Over 2.5", probability = "0.41", odds = "2.35" },
                    new { id = "13", desc = "Under 2.5", probability = "0.59", odds = "1.62" }
                }
            });
        }

        if (includeSetHandicap)
        {
            markets.Add(new
            {
                id = "188",
                product = 3,
                specifier = "hcp=-1.5",
                desc = "Set handicap -1.5",
                name = "Set Handicap",
                marketGuide = "The winner of the match adding or subtracting the indicated set spread to the final result.",
                outcomes = new[]
                {
                    new { id = "1714", desc = "Home (-1.5)", probability = "0.48", odds = "1.95" },
                    new { id = "1715", desc = "Away (+1.5)", probability = "0.52", odds = "1.82" }
                }
            });
            markets.Add(new
            {
                id = "188",
                product = 3,
                specifier = "hcp=1.5",
                desc = "Set handicap 1.5",
                name = "Set Handicap",
                marketGuide = "The winner of the match adding or subtracting the indicated set spread to the final result.",
                outcomes = new[]
                {
                    new { id = "1714", desc = "Home (+1.5)", probability = "0.80", odds = "1.18" },
                    new { id = "1715", desc = "Away (-1.5)", probability = "0.20", odds = "4.80" }
                }
            });
        }

        return JsonSerializer.Serialize(new
        {
            data = new
            {
                eventId,
                homeTeamName = homeTeam,
                awayTeamName = awayTeam,
                estimateStartTime = kickoffUtc.HasValue
                    ? new DateTimeOffset(kickoffUtc.Value).ToUnixTimeMilliseconds()
                    : (long?)null,
                sport = new
                {
                    id = "sr:sport:5",
                    name = "Tennis",
                    category = new
                    {
                        id = "sr:category:72",
                        name = country,
                        tournament = new
                        {
                            id = "sr:tournament:test",
                            name = tournament
                        }
                    }
                },
                markets
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
                    new { id = "1", desc = "Home", probability = "0.55", odds = "1.82" },
                    new { id = "2", desc = "Away", probability = "0.45", odds = "2.20" }
                }
            });
        }

        if (fixture.IncludeTotals)
        {
            markets.Add(new
            {
                id = "18",
                specifier = "total=2.5",
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
        bool IncludeBtts = true);

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class SportyBetTestHandler : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<int, string> _upcomingPages;
        private readonly IReadOnlyDictionary<string, string> _eventDetails;
        private readonly string? _bookingResponse;

        public SportyBetTestHandler(
            IReadOnlyDictionary<int, string>? upcomingPages = null,
            IReadOnlyDictionary<string, string>? eventDetails = null,
            string? bookingResponse = null)
        {
            _upcomingPages = upcomingPages ?? new Dictionary<int, string>();
            _eventDetails = eventDetails ?? new Dictionary<string, string>();
            _bookingResponse = bookingResponse;
        }

        public List<int> RequestedPages { get; } = [];
        public List<string> SharedEventIds { get; } = [];
        public List<string> SharedMarketIds { get; } = [];
        public List<string> SharedOutcomeIds { get; } = [];
        public List<string?> SharedSpecifiers { get; } = [];
        public List<string> RequestedEventDetails { get; } = [];

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

            if (request.Method == HttpMethod.Get && path.Contains("factsCenter/event", StringComparison.Ordinal))
            {
                var eventId = ParseStringQueryValue(request.RequestUri?.Query, "eventId");
                if (!string.IsNullOrWhiteSpace(eventId))
                {
                    RequestedEventDetails.Add(eventId);
                }

                var body = !string.IsNullOrWhiteSpace(eventId) && _eventDetails.TryGetValue(eventId, out var response)
                    ? response
                    : "{\"data\":{}}";

                return CreateJsonResponse(body);
            }

            if (request.Method == HttpMethod.Post && path.Contains("orders/share", StringComparison.Ordinal))
            {
                var payload = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(payload);
                foreach (var selection in document.RootElement.GetProperty("selections").EnumerateArray())
                {
                    SharedEventIds.Add(selection.GetProperty("eventId").GetString() ?? string.Empty);
                    SharedMarketIds.Add(selection.GetProperty("marketId").GetString() ?? string.Empty);
                    SharedOutcomeIds.Add(selection.GetProperty("outcomeId").GetString() ?? string.Empty);
                    SharedSpecifiers.Add(
                        selection.TryGetProperty("specifier", out var specifierElement)
                            ? specifierElement.GetString()
                            : null);
                }

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

        private static string ParseStringQueryValue(string? query, string key)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return string.Empty;
            }

            var trimmed = query.TrimStart('?');
            foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 &&
                    string.Equals(parts[0], key, StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(parts[1]);
                }
            }

            return string.Empty;
        }
    }
}
