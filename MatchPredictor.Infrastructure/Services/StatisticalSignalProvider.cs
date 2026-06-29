using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Persistence;
using MatchPredictor.Infrastructure.Statistics;
using Microsoft.EntityFrameworkCore;

namespace MatchPredictor.Infrastructure.Services;

/// <summary>
/// Builds the trained statistical core (Dixon-Coles goals model + Elo ratings) from
/// finished <see cref="MatchScore"/> history and exposes per-fixture probability
/// signals for the publish pipeline.
///
/// Point-in-time safety: history is restricted to matches that finished strictly
/// before the earliest upcoming kickoff, mirroring <see cref="RegressionPredictorService"/>,
/// so a fixture can never be informed by its own (or any same-slate) result.
/// </summary>
public sealed class StatisticalSignalProvider : IStatisticalSignalProvider
{
    private const int HistoryWindowDays = 540;
    private readonly ApplicationDbContext _db;
    private readonly DixonColesOptions _dixonColesOptions;
    private readonly EloOptions _eloOptions;
    private readonly EnsembleWeights _signalWeights;

    public StatisticalSignalProvider(ApplicationDbContext db)
        : this(db, null, null, null)
    {
    }

    public StatisticalSignalProvider(
        ApplicationDbContext db,
        DixonColesOptions? dixonColesOptions,
        EloOptions? eloOptions,
        EnsembleWeights? signalWeights)
    {
        _db = db;
        _dixonColesOptions = dixonColesOptions ?? new DixonColesOptions();
        _eloOptions = eloOptions ?? new EloOptions();
        // Inside the provider we only blend the two statistical models with each other;
        // the market/base blend happens later in the pipeline.
        _signalWeights = signalWeights ?? new EnsembleWeights { Market = 0, Base = 0, DixonColes = 1.1, Elo = 0.7 };
    }

    public IStatisticalSignalSet BuildSignals(IReadOnlyCollection<MatchData> matches)
    {
        var upcoming = matches
            .Where(match => !string.IsNullOrWhiteSpace(match.HomeTeam) && !string.IsNullOrWhiteSpace(match.AwayTeam))
            .ToList();

        if (upcoming.Count == 0)
        {
            return EmptySignalSet.Instance;
        }

        var nowUtc = DateTime.UtcNow;
        var earliestKickoffUtc = upcoming
            .Where(match => match.MatchDateTime.HasValue)
            .Select(match => match.MatchDateTime!.Value)
            .DefaultIfEmpty(nowUtc)
            .Min();
        var cutoffUtc = earliestKickoffUtc < nowUtc ? earliestKickoffUtc : nowUtc;
        var historyStartUtc = cutoffUtc.AddDays(-HistoryWindowDays);

        var scores = _db.MatchScores
            .AsNoTracking()
            .Where(score => !score.IsLive &&
                            score.MatchTime >= historyStartUtc &&
                            score.MatchTime < cutoffUtc)
            .ToList();

        var results = new List<MatchResult>(scores.Count);
        foreach (var score in scores)
        {
            if (TryParseScore(score.Score, out var homeGoals, out var awayGoals))
            {
                results.Add(new MatchResult(score.HomeTeam, score.AwayTeam, homeGoals, awayGoals, score.MatchTime, score.League));
            }
        }

        if (results.Count == 0)
        {
            return EmptySignalSet.Instance;
        }

        var dixonColes = DixonColesModel.Fit(results, cutoffUtc, _dixonColesOptions);
        var elo = new EloRatingModel(_eloOptions).Train(results);

        var signals = new Dictionary<string, MatchProbabilities>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in upcoming)
        {
            var home = match.HomeTeam!.Trim();
            var away = match.AwayTeam!.Trim();
            var key = BuildKey(home, away);
            if (signals.ContainsKey(key))
            {
                continue;
            }

            // Require enough history for both teams in at least one of the models.
            if (!dixonColes.HasTeam(home) || !dixonColes.HasTeam(away))
            {
                continue;
            }

            var dixonColesSignal = dixonColes.Predict(home, away);

            MatchProbabilities? eloSignal = null;
            if (elo.HasTeam(home) && elo.HasTeam(away))
            {
                var (homeWin, draw, awayWin) = elo.PredictResult(home, away);
                eloSignal = new MatchProbabilities(
                    Btts: dixonColesSignal.Btts,
                    Over25: dixonColesSignal.Over25,
                    Under25: dixonColesSignal.Under25,
                    Draw: draw,
                    HomeWin: homeWin,
                    AwayWin: awayWin);
            }

            signals[key] = EnsembleProbabilityBlender.Blend(
                market: null,
                baseModel: null,
                dixonColes: dixonColesSignal,
                elo: eloSignal,
                weights: _signalWeights);
        }

        return new SignalSet(signals);
    }

    private static string BuildKey(string home, string away) => $"{home}\u0001{away}";

    private static bool TryParseScore(string? score, out int home, out int away)
    {
        home = 0;
        away = 0;
        if (string.IsNullOrWhiteSpace(score))
        {
            return false;
        }

        var normalized = score.Replace("–", "-").Replace("—", "-").Trim();
        var parts = normalized.Contains(':')
            ? normalized.Split(':', StringSplitOptions.TrimEntries)
            : normalized.Split('-', StringSplitOptions.TrimEntries);

        return parts.Length == 2 &&
               int.TryParse(parts[0], out home) &&
               int.TryParse(parts[1], out away);
    }

    private sealed class SignalSet : IStatisticalSignalSet
    {
        private readonly IReadOnlyDictionary<string, MatchProbabilities> _signals;

        public SignalSet(IReadOnlyDictionary<string, MatchProbabilities> signals)
        {
            _signals = signals;
        }

        public MatchProbabilities? GetSignal(MatchData match)
        {
            if (string.IsNullOrWhiteSpace(match.HomeTeam) || string.IsNullOrWhiteSpace(match.AwayTeam))
            {
                return null;
            }

            var key = BuildKey(match.HomeTeam.Trim(), match.AwayTeam.Trim());
            return _signals.TryGetValue(key, out var probabilities) ? probabilities : null;
        }
    }

    private sealed class EmptySignalSet : IStatisticalSignalSet
    {
        public static readonly EmptySignalSet Instance = new();

        public MatchProbabilities? GetSignal(MatchData match) => null;
    }
}
