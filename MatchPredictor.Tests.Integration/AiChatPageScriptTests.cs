using System.Text.RegularExpressions;
using Jint;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class AiChatPageScriptTests
{
    [Fact]
    public void ChatScript_EscapesHtmlBeforeApplyingFormatting()
    {
        var script = ExtractScript(
            "/Users/nnamdi/Desktop/Projects/MatchPredictor/MatchPredictor/MatchPredictor.Web/Pages/AiChat.cshtml");

        var engine = new Engine();
        engine.Execute("""
            var document = {
                body: { appendChild: function() {} },
                getElementById: function() { return null; },
                createElement: function() {
                    return {
                        style: {},
                        dataset: {},
                        classList: { add: function() {}, remove: function() {} },
                        appendChild: function() {},
                        addEventListener: function() {},
                        querySelectorAll: function() { return []; },
                        closest: function() { return null; }
                    };
                },
                addEventListener: function() {}
            };
            var fetch = async function() { return { ok: true, json: async function() { return {}; } }; };
            var addToCart = function() {};
            var openCartModal = function() {};
            """);
        engine.Execute(script);

        var html = engine.Invoke("formatMessageHtml", "<img src=x onerror=alert(1)> **safe**").AsString();

        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html);
        Assert.Contains("<strong>safe</strong>", html);
        Assert.DoesNotContain("<img", html);
    }

    [Fact]
    public void ChatScript_RendersPerActionExplanationsThroughEscapedFormatter()
    {
        var script = ExtractScript(
            "/Users/nnamdi/Desktop/Projects/MatchPredictor/MatchPredictor/MatchPredictor.Web/Pages/AiChat.cshtml");

        Assert.Contains("action.explanation", script);
        Assert.Contains("formatMessageHtml(action.explanation)", script);
    }

    [Fact]
    public void ChatScript_RendersOddsAndStrengthMetadata_ForReturnedActions()
    {
        var script = ExtractScript(
            "/Users/nnamdi/Desktop/Projects/MatchPredictor/MatchPredictor/MatchPredictor.Web/Pages/AiChat.cshtml");

        Assert.Contains("action.estimatedOdds", script);
        Assert.Contains("action.modelProbability", script);
        Assert.Contains("action.marketProbability", script);
        Assert.Contains("action.edgePoints", script);
    }

    private static string ExtractScript(string path)
    {
        var content = File.ReadAllText(path);
        var match = Regex.Match(content, @"<script>([\s\S]*?)</script>", RegexOptions.Singleline);
        Assert.True(match.Success, "Could not find AI Chat page script block.");
        return match.Groups[1].Value;
    }
}
