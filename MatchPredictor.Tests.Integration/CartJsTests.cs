using System.Text.Json;
using Jint;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class CartJsTests
{
    [Fact]
    public void AddToCart_AllowsDifferentMarketsForSameFixture_ButRejectsExactDuplicates()
    {
        var script = File.ReadAllText(ResolveCartJsPath());

        var engine = new Engine();
        engine.Execute("""
            var __storage = {};
            var window = { matchPredictorTracking: { track: function() {} } };
            var localStorage = {
                getItem: function(key) { return Object.prototype.hasOwnProperty.call(__storage, key) ? __storage[key] : null; },
                setItem: function(key, value) { __storage[key] = String(value); },
                removeItem: function(key) { delete __storage[key]; }
            };
            var document = {
                body: { appendChild: function() {} },
                getElementById: function() { return null; },
                createElement: function() {
                    return {
                        id: '',
                        className: '',
                        textContent: '',
                        style: {},
                        classList: { add: function() {}, remove: function() {} },
                        appendChild: function() {}
                    };
                },
                addEventListener: function() {}
            };
            var setTimeout = function(fn) { return 0; };
            """);
        engine.Execute(script);

        engine.Execute("""
            addToCart({ homeTeam: 'Arsenal', awayTeam: 'Chelsea', league: 'England - Premier League', market: 'BTTS', prediction: 'BTTS' });
            addToCart({ homeTeam: 'Arsenal', awayTeam: 'Chelsea', league: 'England - Premier League', market: 'Over2.5', prediction: 'Over 2.5' });
            addToCart({ homeTeam: 'Arsenal', awayTeam: 'Chelsea', league: 'England - Premier League', market: 'BTTS', prediction: 'BTTS' });
            """);

        var cartJson = engine.Evaluate("JSON.stringify(getCart())").AsString();
        using var document = JsonDocument.Parse(cartJson);
        var items = document.RootElement.EnumerateArray().ToList();

        Assert.Equal(2, items.Count);
        Assert.Equal("BTTS", items[0].GetProperty("market").GetString());
        Assert.Equal("Over2.5", items[1].GetProperty("market").GetString());
    }

    [Fact]
    public void AddToCart_DoesNotCollapseSameTeamsWithDifferentKickoffs_AndUsesPredictionIdWhenPresent()
    {
        var script = File.ReadAllText(ResolveCartJsPath());

        var engine = new Engine();
        engine.Execute("""
            var __storage = {};
            var window = { matchPredictorTracking: { track: function() {} } };
            var localStorage = {
                getItem: function(key) { return Object.prototype.hasOwnProperty.call(__storage, key) ? __storage[key] : null; },
                setItem: function(key, value) { __storage[key] = String(value); },
                removeItem: function(key) { delete __storage[key]; }
            };
            var document = {
                body: { appendChild: function() {} },
                getElementById: function() { return null; },
                createElement: function() {
                    return {
                        id: '',
                        className: '',
                        textContent: '',
                        style: {},
                        classList: { add: function() {}, remove: function() {} },
                        appendChild: function() {}
                    };
                },
                addEventListener: function() {}
            };
            var setTimeout = function(fn) { return 0; };
            """);
        engine.Execute(script);

        engine.Execute("""
            addToCart({
                homeTeam: 'Arsenal',
                awayTeam: 'Chelsea',
                league: 'England - Premier League',
                market: 'StraightWin',
                prediction: 'Home Win',
                matchDateTimeUtc: '2030-01-01T12:00:00Z'
            });
            addToCart({
                homeTeam: 'Arsenal',
                awayTeam: 'Chelsea',
                league: 'England - Premier League',
                market: 'StraightWin',
                prediction: 'Home Win',
                matchDateTimeUtc: '2030-01-01T15:00:00Z'
            });
            addToCart({
                homeTeam: 'Arsenal',
                awayTeam: 'Chelsea',
                league: 'England - Premier League',
                market: 'StraightWin',
                prediction: 'Home Win',
                predictionId: 44,
                matchDateTimeUtc: '2030-01-01T18:00:00Z'
            });
            addToCart({
                homeTeam: 'Arsenal',
                awayTeam: 'Chelsea',
                league: 'England - Premier League',
                market: 'StraightWin',
                prediction: 'Home Win',
                predictionId: 44,
                matchDateTimeUtc: '2030-01-01T20:00:00Z'
            });
            """);

        var cartJson = engine.Evaluate("JSON.stringify(getCart())").AsString();
        using var document = JsonDocument.Parse(cartJson);
        var items = document.RootElement.EnumerateArray().ToList();

        Assert.Equal(3, items.Count);
        Assert.Equal("2030-01-01T12:00:00Z", items[0].GetProperty("matchDateTimeUtc").GetString());
        Assert.Equal("2030-01-01T15:00:00Z", items[1].GetProperty("matchDateTimeUtc").GetString());
        Assert.Equal(44, items[2].GetProperty("predictionId").GetInt32());
    }

    [Fact]
    public void CartScript_TracksKeyClientSideEvents()
    {
        var script = File.ReadAllText(ResolveCartJsPath());

        Assert.Contains("matchPredictorTracking?.track('add_to_cart'", script);
        Assert.Contains("matchPredictorTracking?.track('clear_cart'", script);
        Assert.Contains("matchPredictorTracking?.track('open_betslip'", script);
        Assert.Contains("matchPredictorTracking?.track('copy_booking_code'", script);
    }

    [Fact]
    public void OpenSportyBetBooking_DoesNotNavigateCurrentPage_WhenPopupApiReturnsNull()
    {
        var script = File.ReadAllText(ResolveCartJsPath());
        var engine = new Engine();
        engine.Execute("""
            var __storage = {};
            var __openedUrls = [];
            var __anchorClicks = [];
            var __locationHref = 'https://matchpredictor.test/cart';
            var window = {
                matchPredictorTracking: { track: function() {} },
                open: function(url) {
                    __openedUrls.push(url);
                    return null;
                },
                location: {
                    get href() { return __locationHref; },
                    set href(value) { __locationHref = value; }
                }
            };
            var localStorage = {
                getItem: function(key) { return Object.prototype.hasOwnProperty.call(__storage, key) ? __storage[key] : null; },
                setItem: function(key, value) { __storage[key] = String(value); },
                removeItem: function(key) { delete __storage[key]; }
            };
            var document = {
                body: {
                    appendChild: function(node) { this._last = node; },
                    removeChild: function() {}
                },
                getElementById: function(id) {
                    if (id === 'cartBadge' || id === 'cartModal' || id === 'bookingResult') {
                        return { style: {}, textContent: '', classList: { add: function() {}, remove: function() {} }, querySelector: function() { return null; } };
                    }
                    return null;
                },
                createElement: function(tag) {
                    return {
                        tagName: String(tag).toUpperCase(),
                        href: '',
                        target: '',
                        rel: '',
                        style: {},
                        classList: { add: function() {}, remove: function() {} },
                        appendChild: function() {},
                        remove: function() {},
                        click: function() {
                            __anchorClicks.push({ href: this.href, target: this.target, rel: this.rel });
                        }
                    };
                },
                addEventListener: function() {}
            };
            var setTimeout = function(fn) { return 0; };
            """);
        engine.Execute(script);
        engine.Execute("openSportyBetBooking('https://www.sportybet.com/ng/sport/football/share/ABCDE');");

        Assert.Equal("https://matchpredictor.test/cart", engine.Evaluate("__locationHref").AsString());
        Assert.Equal(1, engine.Evaluate("__openedUrls.length").AsNumber());
        Assert.Equal(1, engine.Evaluate("__anchorClicks.length").AsNumber());
        Assert.Equal("_blank", engine.Evaluate("__anchorClicks[0].target").AsString());
        Assert.Contains("noopener", engine.Evaluate("__anchorClicks[0].rel").AsString());
        Assert.DoesNotContain("noopener,noreferrer", script);
        Assert.DoesNotContain("window.location.href = url", script);
    }

    private static string ResolveCartJsPath()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "MatchPredictor.Web", "wwwroot", "js", "cart.js")),
            "/Users/nnamdi/Desktop/Okafor Nelson/Projects/MatchPredictor/MatchPredictor/MatchPredictor.Web/wwwroot/js/cart.js",
            "/Users/nnamdi/Desktop/Projects/MatchPredictor/MatchPredictor/MatchPredictor.Web/wwwroot/js/cart.js"
        };

        return candidates.First(File.Exists);
    }
}
