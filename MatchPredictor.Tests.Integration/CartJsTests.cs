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
}
