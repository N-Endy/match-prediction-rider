using MatchPredictor.Application.Helpers;
using MatchPredictor.Domain.Helpers;
using MatchPredictor.Domain.Models;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class SynergyBetslipComposerTests
{
    [Fact]
    public void Compose_GeneratesAllSixSlips_WithPrivateFlags()
    {
        var candidates = new List<BetslipComposerCandidate>();
        var settings = new SynergySlipSettings();

        var slips = SynergyBetslipComposer.ComposeSynergySlips(candidates, settings);

        Assert.Equal(6, slips.Count);
        Assert.All(slips, slip =>
        {
            Assert.True(slip.IsPrivate);
            Assert.True(slip.IsSynergy);
            Assert.True(BetslipKinds.IsSynergySlip(slip.SlipNumber));
        });

        var slipNumbers = slips.Select(s => s.SlipNumber).ToList();
        Assert.Equal(new[] { 101, 102, 103, 104, 105, 106 }, slipNumbers);
    }

    [Fact]
    public void Compose_Slip1_UnderFortress_RequiresDrawAndUnderThresholds()
    {
        var settings = new SynergySlipSettings
        {
            UnderFortressDrawMin = 0.35,
            UnderFortressUnderMin = 0.65
        };

        var candidates = new List<BetslipComposerCandidate>
        {
            // Fixture 1: Meets both Draw >= 0.35 and Under >= 0.65
            MakeCandidate(1, "fx-1", "Draw", "Draw", 0.36m),
            MakeCandidate(2, "fx-1", "Under2.5Goals", "Under 2.5", 0.70m),

            // Fixture 2: Meets Under but Draw is too low
            MakeCandidate(3, "fx-2", "Draw", "Draw", 0.25m),
            MakeCandidate(4, "fx-2", "Under2.5Goals", "Under 2.5", 0.75m),

            // Fixture 3: Meets Draw but Under is too low
            MakeCandidate(5, "fx-3", "Draw", "Draw", 0.40m),
            MakeCandidate(6, "fx-3", "Under2.5Goals", "Under 2.5", 0.55m)
        };

        var slips = SynergyBetslipComposer.ComposeSynergySlips(candidates, settings);
        var underSlip = slips.First(s => s.SlipNumber == 101);

        Assert.Single(underSlip.Selections);
        Assert.Equal("fx-1", underSlip.Selections[0].FixtureKey);
        Assert.Equal("Under2.5Goals", underSlip.Selections[0].PredictionCategory);
        Assert.Equal("Under 2.5 Fortress", underSlip.Title);
    }

    [Fact]
    public void Compose_Slip2And3_FavoriteMachine_PairsWinAndOver()
    {
        var settings = new SynergySlipSettings
        {
            FavoriteMachineWinMin = 0.75,
            FavoriteMachineOverMin = 0.65
        };

        var candidates = new List<BetslipComposerCandidate>
        {
            // Fixture 1: Home Win 0.80, Over 0.70 -> qualifies
            MakeCandidate(10, "fx-10", "StraightWin", "1", 0.80m),
            MakeCandidate(11, "fx-10", "Over2.5Goals", "Over 2.5", 0.70m),

            // Fixture 2: Win 0.60, Over 0.80 -> win too low
            MakeCandidate(12, "fx-20", "StraightWin", "1", 0.60m),
            MakeCandidate(13, "fx-20", "Over2.5Goals", "Over 2.5", 0.80m)
        };

        var slips = SynergyBetslipComposer.ComposeSynergySlips(candidates, settings);

        var winSlip = slips.First(s => s.SlipNumber == 102);
        var overSlip = slips.First(s => s.SlipNumber == 103);

        Assert.Single(winSlip.Selections);
        Assert.Equal("fx-10", winSlip.Selections[0].FixtureKey);
        Assert.Equal("StraightWin", winSlip.Selections[0].PredictionCategory);
        Assert.Equal("Favorite Machine (Win)", winSlip.Title);

        Assert.Single(overSlip.Selections);
        Assert.Equal("fx-10", overSlip.Selections[0].FixtureKey);
        Assert.Equal("Over2.5Goals", overSlip.Selections[0].PredictionCategory);
        Assert.Equal("Favorite Machine (Over 2.5)", overSlip.Title);
    }

    [Fact]
    public void Compose_Slip4And5_GoalFrenzy_PairsOverAndBtts()
    {
        var settings = new SynergySlipSettings
        {
            GoalFrenzyBttsMin = 0.60,
            GoalFrenzyOverMin = 0.65
        };

        var candidates = new List<BetslipComposerCandidate>
        {
            // Fixture A: BTTS 0.65, Over 0.72 -> qualifies
            MakeCandidate(30, "fx-a", "BothTeamsScore", "BTTS", 0.65m),
            MakeCandidate(31, "fx-a", "Over2.5Goals", "Over 2.5", 0.72m),

            // Fixture B: BTTS 0.55, Over 0.80 -> BTTS too low
            MakeCandidate(32, "fx-b", "BothTeamsScore", "BTTS", 0.55m),
            MakeCandidate(33, "fx-b", "Over2.5Goals", "Over 2.5", 0.80m)
        };

        var slips = SynergyBetslipComposer.ComposeSynergySlips(candidates, settings);

        var overSlip = slips.First(s => s.SlipNumber == 104);
        var bttsSlip = slips.First(s => s.SlipNumber == 105);

        Assert.Single(overSlip.Selections);
        Assert.Equal("fx-a", overSlip.Selections[0].FixtureKey);
        Assert.Equal("Over2.5Goals", overSlip.Selections[0].PredictionCategory);
        Assert.Equal("Goal Frenzy (Over 2.5)", overSlip.Title);

        Assert.Single(bttsSlip.Selections);
        Assert.Equal("fx-a", bttsSlip.Selections[0].FixtureKey);
        Assert.Equal("BothTeamsScore", bttsSlip.Selections[0].PredictionCategory);
        Assert.Equal("Goal Frenzy (BTTS)", bttsSlip.Title);
    }

    [Fact]
    public void Compose_Slip6_DrawBttsStrike_RequiresDrawQualified_And_BttsAbove57Percent()
    {
        var settings = new SynergySlipSettings
        {
            DrawBttsStrikeDrawMin = 0.30,
            DrawBttsStrikeBttsMin = 0.57
        };

        var candidates = new List<BetslipComposerCandidate>
        {
            // Fixture 1: Draw 0.35 (>= 0.30), BTTS 0.58 (>= 0.57) -> qualifies!
            MakeCandidate(40, "fx-draw-1", "Draw", "Draw", 0.35m),
            MakeCandidate(41, "fx-draw-1", "BothTeamsScore", "BTTS", 0.58m),

            // Fixture 2: Draw 0.32, BTTS 0.54 (< 0.57) -> fails BTTS
            MakeCandidate(42, "fx-draw-2", "Draw", "Draw", 0.32m),
            MakeCandidate(43, "fx-draw-2", "BothTeamsScore", "BTTS", 0.54m),

            // Fixture 3: No Draw qualified, BTTS 0.65 -> fails Draw
            MakeCandidate(44, "fx-no-draw", "StraightWin", "1", 0.70m),
            MakeCandidate(45, "fx-no-draw", "BothTeamsScore", "BTTS", 0.65m)
        };

        var slips = SynergyBetslipComposer.ComposeSynergySlips(candidates, settings);
        var strikeSlip = slips.First(s => s.SlipNumber == 106);

        Assert.Single(strikeSlip.Selections);
        Assert.Equal("fx-draw-1", strikeSlip.Selections[0].FixtureKey);
        Assert.Equal("BothTeamsScore", strikeSlip.Selections[0].PredictionCategory);
        Assert.Equal("Draw/BTTS Strike (BTTS)", strikeSlip.Title);
    }

    [Fact]
    public void Compose_ZeroQualifiers_PreservesSlipWithNoticeMessage()
    {
        var settings = new SynergySlipSettings();
        var candidates = new List<BetslipComposerCandidate>(); // Empty

        var slips = SynergyBetslipComposer.ComposeSynergySlips(candidates, settings);

        Assert.Equal(6, slips.Count);
        Assert.All(slips, slip =>
        {
            Assert.Empty(slip.Selections);
            Assert.Equal("No qualifying fixtures today", slip.ShortfallNote);
        });
    }

    private static BetslipComposerCandidate MakeCandidate(
        int id,
        string fixtureKey,
        string category,
        string outcome,
        decimal confidence) =>
        new()
        {
            PredictionId = id,
            FixtureKey = fixtureKey,
            HomeTeam = "Home " + fixtureKey,
            AwayTeam = "Away " + fixtureKey,
            League = "Test League",
            MatchDateTimeUtc = DateTime.UtcNow.AddHours(4),
            Market = category,
            PredictedOutcome = outcome,
            Confidence = confidence,
            PredictionCategory = category,
            ResearchScore = (double)confidence * 100,
            DecimalOdds = 1.85,
            AiNote = null
        };
}
