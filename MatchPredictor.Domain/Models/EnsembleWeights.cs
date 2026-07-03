namespace MatchPredictor.Domain.Models;

/// <summary>
/// Relative weights for each signal feeding the ensemble. Missing signals are
/// skipped and the remaining weights renormalize automatically.
///
/// Signal semantics (see ARCHITECTURE_PREDICTION_BASELINE.md §2):
/// <list type="bullet">
/// <item><b>Bookmaker</b> — de-vigged real bookmaker odds (SportyBet via SourceMarketDeVig).</item>
/// <item><b>Market</b> — the sports-ai.dev prediction feed as shaped by ProbabilityCalculator.
/// Historically named "Market" before real bookmaker pricing existed; it is a third-party
/// model signal, not true market odds.</item>
/// <item><b>Base</b> — reserved for an additional hand-weighted model (currently unused).</item>
/// <item><b>DixonColes</b> — the Dixon-Coles + Elo statistical core.</item>
/// <item><b>Elo</b> — standalone Elo signal (blended inside the statistical core by default).</item>
/// </list>
/// </summary>
public sealed record EnsembleWeights
{
    public double Bookmaker { get; init; } = 0.0;
    public double Market { get; init; } = 1.0;
    public double Base { get; init; } = 0.6;
    public double DixonColes { get; init; } = 1.1;
    public double Elo { get; init; } = 0.7;
    public double Ml { get; init; } = 0.8;

    public static EnsembleWeights Default => new();

    /// <summary>
    /// The weights used by the production candidate builder when no learned per-market
    /// profile has been promoted. Single source of truth shared by DataAnalyzerService
    /// and EnsembleWeightTuningService (which uses it as the incumbent baseline).
    /// </summary>
    public static EnsembleWeights ProductionDefault =>
        new() { Bookmaker = 1.2, Market = 1.0, Base = 0.0, DixonColes = 1.1, Elo = 0.0, Ml = 0.8 };
}
