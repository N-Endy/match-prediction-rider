using MatchPredictor.Domain.Helpers;
using MatchPredictor.Domain.Models;

namespace MatchPredictor.Application.Helpers;

public static class TeamAliasMatchHelper
{
    public static int? ResolveTeamId(
        string? teamName,
        string? league,
        IReadOnlyDictionary<string, int>? aliasLookup)
    {
        if (aliasLookup is null || aliasLookup.Count == 0 || string.IsNullOrWhiteSpace(teamName))
        {
            return null;
        }

        var scopedKey = TeamNameNormalizer.BuildAliasKey(teamName, league);
        if (!string.IsNullOrWhiteSpace(scopedKey) && aliasLookup.TryGetValue(scopedKey, out var scopedTeamId))
        {
            return scopedTeamId;
        }

        var normalized = TeamNameNormalizer.NormalizeAlias(teamName);
        if (!string.IsNullOrWhiteSpace(normalized) && aliasLookup.TryGetValue(normalized, out var globalTeamId))
        {
            return globalTeamId;
        }

        return null;
    }

    public static bool AreCanonicalAliasesEqual(string? leftTeam, string? rightTeam, string? leftLeague, string? rightLeague)
    {
        var leftKey = TeamNameNormalizer.BuildAliasKey(leftTeam, leftLeague);
        var rightKey = TeamNameNormalizer.BuildAliasKey(rightTeam, rightLeague);
        if (!string.IsNullOrWhiteSpace(leftKey) &&
            string.Equals(leftKey, rightKey, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
            TeamNameNormalizer.NormalizeAlias(leftTeam),
            TeamNameNormalizer.NormalizeAlias(rightTeam),
            StringComparison.OrdinalIgnoreCase);
    }

    public static ScoreMatchingHelper.TeamMatchResult GetTeamMatchResult(
        string nameA,
        string nameB,
        string? leagueA,
        string? leagueB,
        IReadOnlyDictionary<string, int>? aliasLookup)
    {
        var teamIdA = ResolveTeamId(nameA, leagueA, aliasLookup);
        var teamIdB = ResolveTeamId(nameB, leagueB, aliasLookup);
        if (teamIdA.HasValue && teamIdB.HasValue && teamIdA.Value == teamIdB.Value)
        {
            return new ScoreMatchingHelper.TeamMatchResult(true, 1.0, true, false);
        }

        if (AreCanonicalAliasesEqual(nameA, nameB, leagueA, leagueB))
        {
            return new ScoreMatchingHelper.TeamMatchResult(true, 1.0, true, false);
        }

        return ScoreMatchingHelper.GetTeamMatchResult(nameA, nameB, leagueA, leagueB);
    }

    public static string FormatRejectionReason(
        FixtureMatchRejectionReason reason,
        string? detail = null)
    {
        var label = reason switch
        {
            FixtureMatchRejectionReason.NoTeamMatch => "NoTeamMatch",
            FixtureMatchRejectionReason.QualifierMismatch => "QualifierMismatch",
            FixtureMatchRejectionReason.BelowFuzzyFloor => "BelowFuzzyFloor",
            FixtureMatchRejectionReason.AmbiguousMargin => "AmbiguousMargin",
            FixtureMatchRejectionReason.ReciprocalMismatch => "ReciprocalMismatch",
            FixtureMatchRejectionReason.DateMiss => "DateMiss",
            FixtureMatchRejectionReason.NoCandidates => "NoCandidates",
            _ => "None"
        };

        return string.IsNullOrWhiteSpace(detail) ? label : $"{label}: {detail}";
    }
}
