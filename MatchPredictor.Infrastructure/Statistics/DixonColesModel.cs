using MatchPredictor.Domain.Models;

namespace MatchPredictor.Infrastructure.Statistics;

public sealed record DixonColesOptions
{
    /// <summary>Half-life (in days) for the exponential time-decay weighting of historical matches.</summary>
    public double HalfLifeDays { get; init; } = 90.0;

    /// <summary>Gradient-ascent iterations for fitting attack/defence/home parameters.</summary>
    public int Iterations { get; init; } = 400;

    /// <summary>Gradient-ascent learning rate.</summary>
    public double LearningRate { get; init; } = 0.05;

    /// <summary>Maximum goals per side considered when building the score matrix.</summary>
    public int MaxGoals { get; init; } = 10;

    /// <summary>Minimum finished matches a team needs before it is considered "known".</summary>
    public int MinMatchesPerTeam { get; init; } = 4;
}

/// <summary>
/// A trained team-strength prediction. Holds per-team attack/defence coefficients,
/// the league baseline, home advantage and the Dixon-Coles low-score correlation
/// parameter, and turns them into market probabilities for a fixture.
/// </summary>
public sealed class DixonColesModel
{
    private readonly DixonColesOptions _options;
    private readonly Dictionary<string, double> _attack;
    private readonly Dictionary<string, double> _defence;
    private readonly HashSet<string> _knownTeams;

    public double Intercept { get; }

    public double HomeAdvantage { get; }

    public double Rho { get; }

    private DixonColesModel(
        DixonColesOptions options,
        double intercept,
        double homeAdvantage,
        double rho,
        Dictionary<string, double> attack,
        Dictionary<string, double> defence,
        HashSet<string> knownTeams)
    {
        _options = options;
        Intercept = intercept;
        HomeAdvantage = homeAdvantage;
        Rho = rho;
        _attack = attack;
        _defence = defence;
        _knownTeams = knownTeams;
    }

    public bool HasTeam(string? team) =>
        !string.IsNullOrWhiteSpace(team) && _knownTeams.Contains(team.Trim());

    public double GetAttack(string team) =>
        _attack.TryGetValue(team.Trim(), out var value) ? value : 0.0;

    public double GetDefence(string team) =>
        _defence.TryGetValue(team.Trim(), out var value) ? value : 0.0;

    /// <summary>Expected goals (lambda, mu) for a fixture.</summary>
    public (double HomeGoals, double AwayGoals) ExpectedGoals(string homeTeam, string awayTeam)
    {
        var home = homeTeam.Trim();
        var away = awayTeam.Trim();
        var logHome = Intercept + HomeAdvantage + GetAttack(home) - GetDefence(away);
        var logAway = Intercept + GetAttack(away) - GetDefence(home);
        return (ClampLambda(Math.Exp(logHome)), ClampLambda(Math.Exp(logAway)));
    }

    /// <summary>Builds the full market probability set for a fixture from the score matrix.</summary>
    public MatchProbabilities Predict(string homeTeam, string awayTeam)
    {
        var (lambda, mu) = ExpectedGoals(homeTeam, awayTeam);
        var max = _options.MaxGoals;
        var homePmf = PoissonPmfVector(lambda, max);
        var awayPmf = PoissonPmfVector(mu, max);

        double homeWin = 0, draw = 0, awayWin = 0, over25 = 0, btts = 0, mass = 0;

        for (var h = 0; h <= max; h++)
        {
            for (var a = 0; a <= max; a++)
            {
                var probability = homePmf[h] * awayPmf[a] * Tau(h, a, lambda, mu, Rho);
                if (probability <= 0)
                {
                    continue;
                }

                mass += probability;

                if (h > a)
                {
                    homeWin += probability;
                }
                else if (h == a)
                {
                    draw += probability;
                }
                else
                {
                    awayWin += probability;
                }

                if (h + a >= 3)
                {
                    over25 += probability;
                }

                if (h > 0 && a > 0)
                {
                    btts += probability;
                }
            }
        }

        if (mass <= 0)
        {
            return new MatchProbabilities(0, 0, 0, 0, 0, 0);
        }

        var over = Math.Clamp(over25 / mass, 0.0, 1.0);
        return new MatchProbabilities(
            Btts: Math.Clamp(btts / mass, 0.0, 1.0),
            Over25: over,
            Under25: Math.Clamp(1.0 - over, 0.0, 1.0),
            Draw: Math.Clamp(draw / mass, 0.0, 1.0),
            HomeWin: Math.Clamp(homeWin / mass, 0.0, 1.0),
            AwayWin: Math.Clamp(awayWin / mass, 0.0, 1.0));
    }

    /// <summary>
    /// Fits the model from finished results using time-decayed Poisson maximum likelihood.
    /// Attack/defence coefficients are mean-centred each step for identifiability, and the
    /// Dixon-Coles rho is estimated by a 1-D search after the means converge.
    /// </summary>
    public static DixonColesModel Fit(
        IReadOnlyCollection<MatchResult> results,
        DateTime asOfUtc,
        DixonColesOptions? options = null)
    {
        options ??= new DixonColesOptions();

        var usable = results
            .Where(match => !string.IsNullOrWhiteSpace(match.HomeTeam) && !string.IsNullOrWhiteSpace(match.AwayTeam))
            .Select(match => new MatchResult(
                match.HomeTeam.Trim(),
                match.AwayTeam.Trim(),
                Math.Max(match.HomeGoals, 0),
                Math.Max(match.AwayGoals, 0),
                match.DateUtc,
                match.League))
            .ToList();

        var teams = usable
            .SelectMany(match => new[] { match.HomeTeam, match.AwayTeam })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var attack = teams.ToDictionary(team => team, _ => 0.0, StringComparer.OrdinalIgnoreCase);
        var defence = teams.ToDictionary(team => team, _ => 0.0, StringComparer.OrdinalIgnoreCase);

        var appearances = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in usable)
        {
            appearances[match.HomeTeam] = appearances.GetValueOrDefault(match.HomeTeam) + 1;
            appearances[match.AwayTeam] = appearances.GetValueOrDefault(match.AwayTeam) + 1;
        }

        var knownTeams = appearances
            .Where(pair => pair.Value >= options.MinMatchesPerTeam)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (usable.Count == 0)
        {
            return new DixonColesModel(options, Math.Log(1.3), 0.25, 0.0, attack, defence, knownTeams);
        }

        var weights = usable
            .Select(match => TimeDecayWeight(match.DateUtc, asOfUtc, options.HalfLifeDays))
            .ToArray();

        var totalWeight = weights.Sum();
        var weightedHomeGoals = usable.Select((match, i) => match.HomeGoals * weights[i]).Sum();
        var weightedAwayGoals = usable.Select((match, i) => match.AwayGoals * weights[i]).Sum();
        var meanHome = Math.Max(weightedHomeGoals / Math.Max(totalWeight, 1e-9), 0.05);
        var meanAway = Math.Max(weightedAwayGoals / Math.Max(totalWeight, 1e-9), 0.05);

        var intercept = Math.Log((meanHome + meanAway) / 2.0);
        var homeAdvantage = Math.Log(meanHome) - Math.Log(meanAway);

        for (var iteration = 0; iteration < options.Iterations; iteration++)
        {
            var gradAttack = teams.ToDictionary(team => team, _ => 0.0, StringComparer.OrdinalIgnoreCase);
            var gradDefence = teams.ToDictionary(team => team, _ => 0.0, StringComparer.OrdinalIgnoreCase);
            var gradIntercept = 0.0;
            var gradHome = 0.0;

            for (var i = 0; i < usable.Count; i++)
            {
                var match = usable[i];
                var weight = weights[i];

                var logHome = intercept + homeAdvantage + attack[match.HomeTeam] - defence[match.AwayTeam];
                var logAway = intercept + attack[match.AwayTeam] - defence[match.HomeTeam];
                var lambda = ClampLambda(Math.Exp(logHome));
                var mu = ClampLambda(Math.Exp(logAway));

                var residualHome = match.HomeGoals - lambda;
                var residualAway = match.AwayGoals - mu;

                gradIntercept += weight * (residualHome + residualAway);
                gradHome += weight * residualHome;
                gradAttack[match.HomeTeam] += weight * residualHome;
                gradAttack[match.AwayTeam] += weight * residualAway;
                gradDefence[match.AwayTeam] -= weight * residualHome;
                gradDefence[match.HomeTeam] -= weight * residualAway;
            }

            var scale = options.LearningRate / Math.Max(totalWeight, 1e-9);
            intercept += scale * gradIntercept;
            homeAdvantage += scale * gradHome;
            homeAdvantage = Math.Clamp(homeAdvantage, 0.0, 1.0);

            foreach (var team in teams)
            {
                attack[team] += scale * gradAttack[team];
                defence[team] += scale * gradDefence[team];
            }

            // Identifiability: keep attack and defence centred on zero.
            var attackMean = attack.Values.Average();
            var defenceMean = defence.Values.Average();
            foreach (var team in teams)
            {
                attack[team] -= attackMean;
                defence[team] -= defenceMean;
            }
        }

        var rho = EstimateRho(usable, weights, intercept, homeAdvantage, attack, defence);

        return new DixonColesModel(options, intercept, homeAdvantage, rho, attack, defence, knownTeams);
    }

    private static double EstimateRho(
        IReadOnlyList<MatchResult> matches,
        IReadOnlyList<double> weights,
        double intercept,
        double homeAdvantage,
        IReadOnlyDictionary<string, double> attack,
        IReadOnlyDictionary<string, double> defence)
    {
        var bestRho = 0.0;
        var bestLogLikelihood = double.NegativeInfinity;

        for (var step = -20; step <= 20; step++)
        {
            var rho = step * 0.01;
            var logLikelihood = 0.0;

            for (var i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                if (match.HomeGoals > 1 || match.AwayGoals > 1)
                {
                    continue;
                }

                var lambda = ClampLambda(Math.Exp(intercept + homeAdvantage + attack[match.HomeTeam] - defence[match.AwayTeam]));
                var mu = ClampLambda(Math.Exp(intercept + attack[match.AwayTeam] - defence[match.HomeTeam]));
                var tau = Tau(match.HomeGoals, match.AwayGoals, lambda, mu, rho);
                logLikelihood += weights[i] * Math.Log(Math.Max(tau, 1e-9));
            }

            if (logLikelihood > bestLogLikelihood)
            {
                bestLogLikelihood = logLikelihood;
                bestRho = rho;
            }
        }

        return bestRho;
    }

    /// <summary>Dixon-Coles low-score dependency adjustment.</summary>
    private static double Tau(int homeGoals, int awayGoals, double lambda, double mu, double rho)
    {
        return (homeGoals, awayGoals) switch
        {
            (0, 0) => 1.0 - (lambda * mu * rho),
            (0, 1) => 1.0 + (lambda * rho),
            (1, 0) => 1.0 + (mu * rho),
            (1, 1) => 1.0 - rho,
            _ => 1.0
        };
    }

    private static double[] PoissonPmfVector(double lambda, int maxGoals)
    {
        var pmf = new double[maxGoals + 1];
        var factorial = 1.0;
        for (var k = 0; k <= maxGoals; k++)
        {
            if (k > 1)
            {
                factorial *= k;
            }

            pmf[k] = Math.Exp(-lambda) * Math.Pow(lambda, k) / factorial;
        }

        return pmf;
    }

    private static double TimeDecayWeight(DateTime matchDateUtc, DateTime asOfUtc, double halfLifeDays)
    {
        var ageDays = Math.Max((asOfUtc - matchDateUtc).TotalDays, 0.0);
        if (halfLifeDays <= 0)
        {
            return 1.0;
        }

        return Math.Pow(0.5, ageDays / halfLifeDays);
    }

    private static double ClampLambda(double value) => Math.Clamp(value, 0.05, 15.0);
}
