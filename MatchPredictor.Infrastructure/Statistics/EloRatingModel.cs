using MatchPredictor.Domain.Models;

namespace MatchPredictor.Infrastructure.Statistics;

/// <summary>
/// Configuration for the Elo rating system.
/// </summary>
public sealed record EloOptions
{
    /// <summary>Base K-factor controlling how fast ratings move per match.</summary>
    public double KFactor { get; init; } = 20.0;

    /// <summary>Rating points added to the home side to model home advantage.</summary>
    public double HomeAdvantage { get; init; } = 60.0;

    /// <summary>Logistic scale (classic Elo uses 400).</summary>
    public double Scale { get; init; } = 400.0;

    /// <summary>Starting rating for an unseen team.</summary>
    public double InitialRating { get; init; } = 1500.0;

    /// <summary>When true, larger winning margins move ratings more (FiveThirtyEight style).</summary>
    public bool UseGoalDifferenceMultiplier { get; init; } = true;

    /// <summary>Maximum share of points (0-0.5) assigned to a draw at equal strength.</summary>
    public double MaxDrawProbability { get; init; } = 0.30;

    /// <summary>Controls how quickly draw probability falls off as the rating gap widens.</summary>
    public double DrawWidth { get; init; } = 220.0;
}

/// <summary>
/// A self-contained Elo rating system for football teams. Ratings are an
/// <em>independent</em> strength signal (no market odds) that complements the
/// Dixon-Coles goals model inside the ensemble.
/// </summary>
public sealed class EloRatingModel
{
    private readonly EloOptions _options;
    private readonly Dictionary<string, double> _ratings = new(StringComparer.OrdinalIgnoreCase);

    public EloRatingModel(EloOptions? options = null)
    {
        _options = options ?? new EloOptions();
    }

    public IReadOnlyDictionary<string, double> Ratings => _ratings;

    public bool HasTeam(string team) => !string.IsNullOrWhiteSpace(team) && _ratings.ContainsKey(team.Trim());

    public double GetRating(string team)
    {
        if (string.IsNullOrWhiteSpace(team))
        {
            return _options.InitialRating;
        }

        return _ratings.TryGetValue(team.Trim(), out var rating) ? rating : _options.InitialRating;
    }

    /// <summary>Trains the ratings by replaying results in chronological order.</summary>
    public EloRatingModel Train(IEnumerable<MatchResult> results)
    {
        foreach (var result in results.OrderBy(match => match.DateUtc))
        {
            Update(result);
        }

        return this;
    }

    /// <summary>Applies a single result, mutating the home and away ratings.</summary>
    public void Update(MatchResult result)
    {
        if (string.IsNullOrWhiteSpace(result.HomeTeam) || string.IsNullOrWhiteSpace(result.AwayTeam))
        {
            return;
        }

        var home = result.HomeTeam.Trim();
        var away = result.AwayTeam.Trim();
        var homeRating = GetRating(home);
        var awayRating = GetRating(away);

        var expectedHome = ExpectedScore(homeRating + _options.HomeAdvantage, awayRating);
        var actualHome = result.HomeGoals > result.AwayGoals ? 1.0
            : result.HomeGoals == result.AwayGoals ? 0.5
            : 0.0;

        var k = _options.KFactor;
        if (_options.UseGoalDifferenceMultiplier)
        {
            k *= GoalDifferenceMultiplier(result.HomeGoals - result.AwayGoals, homeRating + _options.HomeAdvantage - awayRating);
        }

        var delta = k * (actualHome - expectedHome);
        _ratings[home] = homeRating + delta;
        _ratings[away] = awayRating - delta;
    }

    /// <summary>Expected score (point share in [0,1]) of A versus B given their ratings.</summary>
    public double ExpectedScore(double ratingA, double ratingB)
    {
        return 1.0 / (1.0 + Math.Pow(10.0, (ratingB - ratingA) / _options.Scale));
    }

    /// <summary>
    /// Predicts a 1X2 distribution. The expected point-share identity
    /// (P(home) + 0.5 * P(draw) == expectedScore) is preserved exactly, and the
    /// draw probability shrinks as the rating gap widens.
    /// </summary>
    public (double HomeWin, double Draw, double AwayWin) PredictResult(string homeTeam, string awayTeam)
    {
        var homeRating = GetRating(homeTeam) + _options.HomeAdvantage;
        var awayRating = GetRating(awayTeam);
        var expectedHome = ExpectedScore(homeRating, awayRating);

        var gap = homeRating - awayRating;
        var drawProbability = _options.MaxDrawProbability * Math.Exp(-Math.Pow(gap / _options.DrawWidth, 2));
        drawProbability = Math.Clamp(drawProbability, 0.0, 0.6);

        var homeWin = expectedHome - (0.5 * drawProbability);
        var awayWin = 1.0 - drawProbability - homeWin;

        homeWin = Math.Clamp(homeWin, 0.0, 1.0);
        awayWin = Math.Clamp(awayWin, 0.0, 1.0);
        var total = homeWin + drawProbability + awayWin;
        if (total <= 0)
        {
            return (1.0 / 3.0, 1.0 / 3.0, 1.0 / 3.0);
        }

        return (homeWin / total, drawProbability / total, awayWin / total);
    }

    private double GoalDifferenceMultiplier(int goalDifference, double ratingDifference)
    {
        var margin = Math.Abs(goalDifference);
        if (margin <= 1)
        {
            return 1.0;
        }

        // Dampen the multiplier for results that were already expected (auto-correlation
        // correction), following the FiveThirtyEight formulation.
        var winnerRatingEdge = goalDifference > 0 ? ratingDifference : -ratingDifference;
        return Math.Log(margin + 1.0) * (2.2 / ((winnerRatingEdge * 0.001) + 2.2));
    }
}
