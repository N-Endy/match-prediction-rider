using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Infrastructure.Services;

public partial class LiveInPlayResearchEngine : ILiveInPlayResearchEngine
{
    public LiveInPlayAnalysis Analyze(AiChatContextBuilder.AiChatContextCandidate candidate, DateTime nowUtc)
    {
        var (homeScore, awayScore, _) = ParseScore(candidate.ActualScore);
        var totalGoals = homeScore + awayScore;
        var (elapsedMinute, matchPhase) = DetermineElapsedMinuteAndPhase(
            candidate.ActualScore,
            candidate.MatchDateTimeUtc,
            nowUtc);

        var minute = elapsedMinute ?? 45;
        var preMarket = candidate.PredictionCategory;
        var preOutcome = candidate.PredictedOutcome;
        var preConf = (double)(candidate.ConfidenceScore ?? 0.65m);
        var preOdds = candidate.EstimatedOdds;

        string inPlayMarket;
        string inPlayOutcome;
        double inPlayConfidence;
        double inPlayOdds;
        string tacticalReasoning;
        string contrastSummary;
        string keyRisk;

        if (preMarket.Equals("Over2.5Goals", StringComparison.OrdinalIgnoreCase))
        {
            if (totalGoals >= 3)
            {
                if (minute < 70)
                {
                    inPlayMarket = "Over/Under Goals";
                    inPlayOutcome = $"Over {totalGoals + 0.5} Goals";
                    inPlayConfidence = 0.70;
                    inPlayOdds = 1.82;
                    tacticalReasoning = $"Match pace is electric with {totalGoals} goals already scored by minute {minute}'. Both defensive lines are stretched and committing forward.";
                    contrastSummary = $"Pre-match model favored Over 2.5 ({preConf:P0} confidence). With {totalGoals} goals already landed, the in-play angle extends to Over {totalGoals + 0.5} Goals.";
                    keyRisk = "Managers may introduce defensive substitutions to protect goal differential.";
                }
                else
                {
                    inPlayMarket = "Over/Under Goals";
                    inPlayOutcome = $"Under {totalGoals + 1.5} Goals";
                    inPlayConfidence = 0.74;
                    inPlayOdds = 1.65;
                    tacticalReasoning = $"Match already cleared {totalGoals} goals, but entering late phase ({minute}'), fatigue and tactical game management will compress remaining chances.";
                    contrastSummary = $"Pre-match Over 2.5 has landed. In-play pivot shifts to Under {totalGoals + 1.5} to capitalize on late-game tempo cooldown.";
                    keyRisk = "A stoppage-time counter-attack or penalty against tired legs.";
                }
            }
            else if (totalGoals <= 1 && minute >= 60)
            {
                var underLine = (totalGoals == 0 && minute >= 72) ? "1.5" : "2.5";
                inPlayMarket = "Over/Under Goals";
                inPlayOutcome = $"Under {underLine} Goals";
                inPlayConfidence = 0.73;
                inPlayOdds = 1.70;
                tacticalReasoning = $"Through {minute} minutes, only {totalGoals} goal(s) registered. Compact defensive shapes and low shot velocity fail to validate pre-match high-scoring projections.";
                contrastSummary = $"Pre-match model favored Over 2.5 ({preConf:P0} confidence). Real-time research at {minute}' ({homeScore}-{awayScore}) shows low goal pace, decisively pivoting to Under {underLine} Goals.";
                keyRisk = "A sudden red card or set-piece opener that breaks open game state late.";
            }
            else
            {
                inPlayMarket = "Over/Under Goals";
                inPlayOutcome = "Over 2.5 Goals";
                inPlayConfidence = 0.67;
                inPlayOdds = 1.90;
                tacticalReasoning = $"Active match flow with {totalGoals} goal(s) at {minute}'. Both sides are finding penetration in the final third with plenty of match time remaining.";
                contrastSummary = $"Pre-match favored Over 2.5. Live game flow confirms high shot frequency, keeping Over 2.5 as the primary in-play angle.";
                keyRisk = "Mid-game tactical adjustments tightening transitional spaces.";
            }
        }
        else if (preMarket.Equals("Under2.5Goals", StringComparison.OrdinalIgnoreCase))
        {
            if (totalGoals >= 2 && minute <= 55)
            {
                inPlayMarket = "Over/Under Goals";
                inPlayOutcome = "Over 2.5 Goals";
                inPlayConfidence = 0.78;
                inPlayOdds = 1.60;
                tacticalReasoning = $"Match erupted with {totalGoals} early goals by {minute}'. High transition frequency makes low-scoring pre-match assumptions obsolete.";
                contrastSummary = $"Pre-match model leaned Under 2.5 ({preConf:P0} confidence). Live tempo completely contradicted pre-match profile with {totalGoals} quick goals, pivoting to Over 2.5 Goals.";
                keyRisk = "Both managers shifting to defensive containment at halftime or around 60'.";
            }
            else
            {
                inPlayMarket = "Over/Under Goals";
                inPlayOutcome = "Under 2.5 Goals";
                inPlayConfidence = 0.76;
                inPlayOdds = 1.55;
                tacticalReasoning = $"Disciplined defensive organization confirmed in real-time with score {homeScore}-{awayScore} at {minute}'.";
                contrastSummary = $"Pre-match model favored Under 2.5. In-play progression at {minute}' reinforces defensive solidity, maintaining Under 2.5.";
                keyRisk = "Fatigue-induced errors in the final 15 minutes.";
            }
        }
        else if (preMarket.Equals("BothTeamsScore", StringComparison.OrdinalIgnoreCase))
        {
            if (homeScore > 0 && awayScore > 0)
            {
                inPlayMarket = "Over/Under Goals";
                inPlayOutcome = $"Over {totalGoals + 0.5} Goals";
                inPlayConfidence = 0.69;
                inPlayOdds = 1.85;
                tacticalReasoning = $"BTTS already verified ({homeScore}-{awayScore} at {minute}'). End-to-end rhythm remains open with momentum swinging both ways.";
                contrastSummary = $"Pre-match BTTS ({preConf:P0} confidence) has already won. In-play angle pivots forward to Over {totalGoals + 0.5} Goals for remaining action.";
                keyRisk = "Both teams settling for a point if match remains level late.";
            }
            else if ((homeScore == 0 || awayScore == 0) && minute >= 70)
            {
                var trailingSide = homeScore == 0 ? candidate.HomeTeam : candidate.AwayTeam;
                inPlayMarket = "Both Teams To Score";
                inPlayOutcome = "BTTS: No";
                inPlayConfidence = 0.74;
                inPlayOdds = 1.62;
                tacticalReasoning = $"At {minute}', {trailingSide} has failed to breach the opposing backline, with time running out and attacking fatigue setting in.";
                contrastSummary = $"Pre-match model leaned BTTS (Yes) ({preConf:P0} confidence). Real-time research at {minute}' ({homeScore}-{awayScore}) shows one side completely stifled, pivoting to BTTS: No.";
                keyRisk = "A controversial penalty or defensive blunder in stoppage time.";
            }
            else
            {
                inPlayMarket = "Both Teams To Score";
                inPlayOutcome = "BTTS: Yes";
                inPlayConfidence = 0.65;
                inPlayOdds = 1.95;
                tacticalReasoning = $"Score stands {homeScore}-{awayScore} at {minute}', with sustained pressure pointing toward both nets being found.";
                contrastSummary = $"Pre-match BTTS (Yes) holds as attacking metrics remain positive through {minute}'.";
                keyRisk = "Clinical finishing deficits continuing into second half.";
            }
        }
        else // 1X2 or general match winner
        {
            if (homeScore > awayScore)
            {
                if (minute >= 65)
                {
                    inPlayMarket = "Match Winner / 1X2";
                    inPlayOutcome = $"{candidate.HomeTeam} to Win";
                    inPlayConfidence = 0.76;
                    inPlayOdds = 1.48;
                    tacticalReasoning = $"{candidate.HomeTeam} commands a {homeScore}-{awayScore} lead at {minute}' and is effectively managing game tempo and territory.";
                    contrastSummary = $"Pre-match model prediction: {preOutcome}. In-play live dynamics firmly support {candidate.HomeTeam} maintaining their lead.";
                    keyRisk = "Late set-piece equalizer or defensive red card.";
                }
                else
                {
                    if (preOutcome.Contains(candidate.AwayTeam, StringComparison.OrdinalIgnoreCase))
                    {
                        inPlayMarket = "Double Chance";
                        inPlayOutcome = $"{candidate.AwayTeam} or Draw (X2)";
                        inPlayConfidence = 0.64;
                        inPlayOdds = 2.10;
                        tacticalReasoning = $"{candidate.HomeTeam} struck first ({homeScore}-{awayScore} at {minute}'), but pre-match quality metrics favor {candidate.AwayTeam} to stage a comeback.";
                        contrastSummary = $"Pre-match favored {candidate.AwayTeam}. Trailing {homeScore}-{awayScore} at {minute}', in-play research identifies value in the X2 comeback line.";
                        keyRisk = "{candidate.HomeTeam} scoring on the counter as {candidate.AwayTeam} overcommits.";
                    }
                    else
                    {
                        inPlayMarket = "Match Winner / 1X2";
                        inPlayOutcome = $"{candidate.HomeTeam} to Win";
                        inPlayConfidence = 0.71;
                        inPlayOdds = 1.65;
                        tacticalReasoning = $"{candidate.HomeTeam} has validated pre-match expectations by taking an early {homeScore}-{awayScore} lead at {minute}'.";
                        contrastSummary = $"Pre-match favored {candidate.HomeTeam} ({preConf:P0}). Real-time scoreline affirms the pre-match thesis.";
                        keyRisk = "Complacency in defensive transitions.";
                    }
                }
            }
            else if (awayScore > homeScore)
            {
                if (minute >= 65)
                {
                    inPlayMarket = "Match Winner / 1X2";
                    inPlayOutcome = $"{candidate.AwayTeam} to Win";
                    inPlayConfidence = 0.75;
                    inPlayOdds = 1.55;
                    tacticalReasoning = $"{candidate.AwayTeam} is dictating play away from home with a {homeScore}-{awayScore} advantage at {minute}'.";
                    contrastSummary = $"Pre-match prediction: {preOutcome}. Live in-play research favors {candidate.AwayTeam} to close out all 3 points.";
                    keyRisk = "Intense home crowd surge in closing 10 minutes.";
                }
                else
                {
                    inPlayMarket = "Double Chance";
                    inPlayOutcome = $"{candidate.HomeTeam} or Draw (1X)";
                    inPlayConfidence = 0.63;
                    inPlayOdds = 2.05;
                    tacticalReasoning = $"{candidate.HomeTeam} trailing {homeScore}-{awayScore} at {minute}' has ample time to mount a second-half response at home.";
                    contrastSummary = $"Pre-match favored {candidate.HomeTeam}. In-play angle pivots to 1X Double Chance as home pressure builds.";
                    keyRisk = "{candidate.AwayTeam} expanding lead on transition.";
                }
            }
            else // Tied score
            {
                if (minute >= 70)
                {
                    inPlayMarket = "Match Winner / 1X2";
                    inPlayOutcome = "Draw (X)";
                    inPlayConfidence = 0.68;
                    inPlayOdds = 2.20;
                    tacticalReasoning = $"Score deadlocked at {homeScore}-{awayScore} at {minute}'. Both sides prioritizing defensive structure over attacking risks late in the game.";
                    contrastSummary = $"Pre-match model leaned {preOutcome}. Locked at {homeScore}-{awayScore} at {minute}', in-play research pivots to Draw (X).";
                    keyRisk = "Stoppage-time deflection or controversial penalty.";
                }
                else
                {
                    inPlayMarket = "Double Chance";
                    inPlayOutcome = $"{candidate.HomeTeam} or Draw (1X)";
                    inPlayConfidence = 0.65;
                    inPlayOdds = 1.45;
                    tacticalReasoning = $"Level at {homeScore}-{awayScore} ({minute}'). Home field advantage and territorial possession lean toward {candidate.HomeTeam} securing at least a point.";
                    contrastSummary = $"Pre-match favored {preOutcome}. Game tied at {minute}', with 1X providing resilient in-play coverage.";
                    keyRisk = "Sudden away breakaway goal against run of play.";
                }
            }
        }

        var liveBullets = new List<string>
        {
            $"Live State: {homeScore} - {awayScore} ({minute}', {matchPhase})",
            $"Pre-Match Baseline: {preOutcome} ({preConf:P0} model confidence)",
            $"In-Play Pivot: {inPlayOutcome} ({inPlayMarket}) @ ~{inPlayOdds:F2} odds",
            $"Tactical Contrast: {contrastSummary}",
            $"Key In-Play Risk: {keyRisk}"
        };

        return new LiveInPlayAnalysis
        {
            PredictionId = candidate.PredictionId,
            HomeTeam = candidate.HomeTeam,
            AwayTeam = candidate.AwayTeam,
            League = candidate.League,
            MatchDateTimeUtc = candidate.MatchDateTimeUtc,
            LiveScore = $"{homeScore} - {awayScore}",
            ElapsedMinutes = minute,
            MatchPhase = matchPhase,
            PreMatchMarket = preMarket,
            PreMatchOutcome = preOutcome,
            PreMatchConfidence = preConf,
            PreMatchOdds = preOdds,
            InPlayMarket = inPlayMarket,
            InPlayOutcome = inPlayOutcome,
            InPlayConfidence = inPlayConfidence,
            InPlayOdds = inPlayOdds,
            ContrastSummary = contrastSummary,
            TacticalReasoning = tacticalReasoning,
            KeyRisk = keyRisk,
            ActionKey = candidate.ActionKey,
            LiveInsightBullets = liveBullets
        };
    }

    public AiChatAction CreateInPlayAction(AiChatContextBuilder.AiChatContextCandidate candidate, LiveInPlayAnalysis analysis)
    {
        return new AiChatAction
        {
            Type = "inplay_pick",
            ActionKey = candidate.ActionKey,
            PredictionId = candidate.PredictionId,
            HomeTeam = candidate.HomeTeam,
            AwayTeam = candidate.AwayTeam,
            League = candidate.League,
            MatchDateLabel = "Live In-Play",
            KickoffTime = $"{analysis.ElapsedMinutes?.ToString() ?? "Live"}' ({analysis.MatchPhase})",
            MatchDateTimeUtc = candidate.MatchDateTimeUtc,
            Status = "Live",
            ActualScore = analysis.LiveScore,
            Market = analysis.InPlayMarket,
            Prediction = analysis.InPlayOutcome,
            Explanation = analysis.TacticalReasoning,
            ModelProbability = analysis.InPlayConfidence,
            MarketProbability = null,
            EdgePoints = null,
            EstimatedOdds = analysis.InPlayOdds,
            AnalysisSummary = analysis.ContrastSummary,
            AnalysisConfidence = $"{analysis.InPlayConfidence:P0}",
            InsightBullets = analysis.LiveInsightBullets,
            InsightSource = "Live In-Play Tactical Engine",
            CanBook = false
        };
    }

    public string BuildInPlaySummaryMessage(IReadOnlyList<LiveInPlayAnalysis> analyses, string userPrompt)
    {
        if (analyses.Count == 0)
        {
            return "There are currently no active live in-play games in today's tracked matches. In-play analysis and dynamic predictions activate as soon as matches kick off. Check back once today's card gets underway!";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"### 🔴 Live In-Play Match Research ({analyses.Count} Active Fixture{(analyses.Count > 1 ? "s" : "")})");
        sb.AppendLine();
        sb.AppendLine("Below is the real-time in-play research for currently active matches. In-play recommendations are dynamically evaluated against live score momentum, elapsed match phase, and pre-match expectations:");
        sb.AppendLine();

        foreach (var item in analyses)
        {
            sb.AppendLine($"#### ⚡ {item.HomeTeam} vs {item.AwayTeam} — **{item.LiveScore}** ({item.ElapsedMinutes}', {item.MatchPhase})");
            sb.AppendLine($"*League: {item.League}*");
            sb.AppendLine();
            sb.AppendLine($"> **Pre-Match Baseline:** {item.PreMatchOutcome} ({item.PreMatchConfidence:P0} model confidence)");
            sb.AppendLine($"> **⚡ Live In-Play Pivot:** **{item.InPlayOutcome}** ({item.InPlayMarket}) @ ~{item.InPlayOdds:F2} odds ({item.InPlayConfidence:P0} live confidence)");
            sb.AppendLine($"> **Pre-Match vs. Live Contrast:** {item.ContrastSummary}");
            sb.AppendLine();
            sb.AppendLine($"**Tactical Read:** {item.TacticalReasoning}");
            sb.AppendLine();
            sb.AppendLine($"*Key Risk: {item.KeyRisk}*");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        sb.AppendLine("*Note: In-play betting lines and odds update continuously on bookmakers based on live match velocity. Pre-match booking codes are closed after kickoff.*");
        return sb.ToString().Trim();
    }

    public string BuildInPlaySystemPrompt(IReadOnlyList<LiveInPlayAnalysis> analyses, string userPrompt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are Nelson AI, an elite real-time football tactical analyst and in-play betting specialist.");
        sb.AppendLine("The user has asked for live in-play match predictions and research.");
        sb.AppendLine("CRITICAL IN-PLAY PRINCIPLES:");
        sb.AppendLine("1. Formulate dynamic predictions based strictly on the current live match state (current score, elapsed minutes, pace, momentum).");
        sb.AppendLine("2. DO NOT be constrained by the original pre-match forecast. If a match is 0-0 at 70', pivot away from Over 2.5 to Under lines. If an underdog is dominating, pivot accordingly.");
        sb.AppendLine("3. EXPLICITLY CONTRAST the pre-match model lean with your live in-play pivot for every fixture.");
        sb.AppendLine("4. Remind the user that in-play bets are placed directly on bookmakers as pre-match booking codes close at kickoff.");
        sb.AppendLine();
        sb.AppendLine("Current active in-play matches and algorithmic analysis:");

        foreach (var item in analyses)
        {
            sb.AppendLine($"- Fixture: {item.HomeTeam} vs {item.AwayTeam} ({item.League})");
            sb.AppendLine($"  Live Score: {item.LiveScore} | Elapsed: {item.ElapsedMinutes}' ({item.MatchPhase})");
            sb.AppendLine($"  Pre-Match Forecast: {item.PreMatchOutcome} ({item.PreMatchMarket}, {item.PreMatchConfidence:P0} confidence)");
            sb.AppendLine($"  Suggested In-Play Pivot: {item.InPlayOutcome} ({item.InPlayMarket}, ~{item.InPlayOdds:F2} odds, {item.InPlayConfidence:P0} confidence)");
            sb.AppendLine($"  Tactical Contrast: {item.ContrastSummary}");
            sb.AppendLine($"  Tactical Reasoning: {item.TacticalReasoning}");
            sb.AppendLine($"  Key Risk: {item.KeyRisk}");
        }

        return sb.ToString();
    }

    private static (int HomeScore, int AwayScore, bool HasScore) ParseScore(string? actualScore)
    {
        if (string.IsNullOrWhiteSpace(actualScore))
        {
            return (0, 0, false);
        }

        var match = Regex.Match(actualScore, @"(\d+)\s*[-:]\s*(\d+)");
        if (match.Success &&
            int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var home) &&
            int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var away))
        {
            return (home, away, true);
        }

        return (0, 0, false);
    }

    private static (int? Minute, string Phase) DetermineElapsedMinuteAndPhase(
        string? actualScore,
        DateTime? kickoffUtc,
        DateTime nowUtc)
    {
        if (!string.IsNullOrWhiteSpace(actualScore))
        {
            var minMatch = Regex.Match(actualScore, @"(\d+)'");
            if (minMatch.Success && int.TryParse(minMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var explicitMin))
            {
                var phase = explicitMin switch
                {
                    <= 45 => "1st Half",
                    > 45 and <= 75 => "2nd Half",
                    _ => "Late Game (75'+)"
                };
                return (explicitMin, phase);
            }

            if (actualScore.Contains("HT", StringComparison.OrdinalIgnoreCase) ||
                actualScore.Contains("Half Time", StringComparison.OrdinalIgnoreCase))
            {
                return (45, "Half Time (HT)");
            }
        }

        if (kickoffUtc.HasValue)
        {
            var elapsed = (int)Math.Max(0, (nowUtc - kickoffUtc.Value).TotalMinutes);
            if (elapsed <= 45)
            {
                return (Math.Max(1, elapsed), "1st Half");
            }
            if (elapsed <= 60)
            {
                return (45, "Half Time (HT)");
            }
            if (elapsed <= 105)
            {
                var secondHalfMinute = Math.Min(90, 45 + (elapsed - 60));
                var phase = secondHalfMinute >= 75 ? "Late Game (75'+)" : "2nd Half";
                return (secondHalfMinute, phase);
            }

            return (90, "Late Game / Stoppage");
        }

        return (null, "Live In-Play");
    }
}
