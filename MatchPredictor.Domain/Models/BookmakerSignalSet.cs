namespace MatchPredictor.Domain.Models;

/// <summary>
/// Per-fixture de-vigged bookmaker probabilities for one prediction batch, keyed by the
/// exact <see cref="MatchData"/> instances the batch was built from. Built by the caller
/// (which owns fixture matching against the pricing source) and consumed by the candidate
/// builder as the true market signal in the ensemble.
/// </summary>
public sealed class BookmakerSignalSet
{
    public static readonly BookmakerSignalSet Empty = new();

    private readonly Dictionary<MatchData, PartialMatchProbabilities> _signals = new();

    public int Count => _signals.Count;

    public void Add(MatchData match, PartialMatchProbabilities signal)
    {
        if (signal.HasAnySignal)
        {
            _signals[match] = signal;
        }
    }

    public PartialMatchProbabilities? GetSignal(MatchData match)
    {
        return _signals.TryGetValue(match, out var signal) ? signal : null;
    }
}
