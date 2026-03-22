using System.Text.Json;
using Jint;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class CartJsTests
{
    [Fact]
    public void AddToCart_AllowsDifferentMarketsForSameFixture_ButRejectsExactDuplicates()
    {
        var script = File.ReadAllText(
            "/Users/nnamdi/Desktop/Projects/MatchPredictor/MatchPredictor/MatchPredictor.Web/wwwroot/js/cart.js");

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
        var script = File.ReadAllText(
            "/Users/nnamdi/Desktop/Projects/MatchPredictor/MatchPredictor/MatchPredictor.Web/wwwroot/js/cart.js");

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
        var script = File.ReadAllText(
            "/Users/nnamdi/Desktop/Projects/MatchPredictor/MatchPredictor/MatchPredictor.Web/wwwroot/js/cart.js");

        Assert.Contains("matchPredictorTracking?.track('add_to_cart'", script);
        Assert.Contains("matchPredictorTracking?.track('add_many_to_cart'", script);
        Assert.Contains("matchPredictorTracking?.track('clear_cart'", script);
        Assert.Contains("matchPredictorTracking?.track('open_betslip'", script);
        Assert.Contains("matchPredictorTracking?.track('copy_booking_code'", script);
    }

    [Fact]
    public void AddManyToCart_AddsUniqueItemsAndCanOpenCart()
    {
        var script = File.ReadAllText(
            "/Users/nnamdi/Desktop/Projects/MatchPredictor/MatchPredictor/MatchPredictor.Web/wwwroot/js/cart.js");

        var engine = new Engine();
        engine.Execute("""
            var __storage = {};
            var __opened = 0;
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
            var openCartModal = function() { __opened += 1; };
            """);
        engine.Execute(script);

        engine.Execute("""
            addManyToCart([
                { homeTeam: 'Ben Shelton', awayTeam: 'Alexander Shevchenko', league: 'ATP Miami Open - R2', market: 'MatchWinner', prediction: 'Home Win', predictionId: 1 },
                { homeTeam: 'Ben Shelton', awayTeam: 'Alexander Shevchenko', league: 'ATP Miami Open - R2', market: 'MatchWinner', prediction: 'Home Win', predictionId: 1 },
                { homeTeam: 'Jie Cui', awayTeam: 'Yuki Mochizuki', league: 'ATP Challenger Yokkaichi - Qualifiers', market: 'OverUnderSets', prediction: 'Under 2.5 Sets', predictionId: 2 }
            ], { openCart: true, source: 'ai_chat' });
            """);

        var cartJson = engine.Evaluate("JSON.stringify(getCart())").AsString();
        using var document = JsonDocument.Parse(cartJson);
        var items = document.RootElement.EnumerateArray().ToList();

        Assert.Equal(2, items.Count);
        Assert.Equal(1, engine.Evaluate("__opened").AsNumber());
    }
}
