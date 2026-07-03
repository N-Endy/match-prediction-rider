using MatchPredictor.Domain.Models;

namespace MatchPredictor.Infrastructure.Statistics;

public sealed record DixonColesOptions
{
    /// <summary>Half-life (in days) for the exponential time-decay weighting of historical matches.</summary>
    public double HalfLifeDays { get; init; } = 90.0;

    /// <summary>Maximum adaptive-gradient iterations for fitting attack/defence/home parameters.</summary>
    public int Iterations { get; init; } = 400;

    /// <summary>Adaptive-gradient learning rate.</summary>
    public double LearningRate { get; init; } = 0.05;

    /// <summary>Stop fitting when weighted log-likelihood improvement falls below this value.</summary>
    public double ConvergenceTolerance { get; init; } = 1e-5;

    /// <summary>Maximum goals per side considered when building the score matrix.</summary>
    public int MaxGoals { get; init; } = 10;

    /// <summary>Minimum finished matches a team needs before it is considered "known".</summary>
    public int MinMatchesPerTeam { get; init; } = 4;

    /// <summary>Minimum finished matches needed before fitting a league-specific submodel.</summary>
    public int MinMatchesPerLeague { get; init; } = 16;

    public double[] HalfLifeCandidateDays { get; init; } = [45.0, 60.0, 90.0, 120.0, 180.0];

    public double HalfLifePromotionBrierTolerance { get; init; } = 0.001;
}

public sealed record DixonColesHalfLifeTuningResult(
    double SelectedHalfLifeDays,
    double IncumbentHalfLifeDays,
    double IncumbentBrier,
    double CandidateBrier,
    bool Promoted);

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
    private readonly Dictionary<string, DixonColesModel> _leagueModels;

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
        HashSet<string> knownTeams,
        Dictionary<string, DixonColesModel>? leagueModels = null)
    {
        _options = options;
        Intercept = intercept;
        HomeAdvantage = homeAdvantage;
        Rho = rho;
        _attack = attack;
        _defence = defence;
        _knownTeams = knownTeams;
        _leagueModels = leagueModels ?? new Dictionary<string, DixonColesModel>(StringComparer.OrdinalIgnoreCase);
    }

    public bool HasTeam(string? team) =>
        !string.IsNullOrWhiteSpace(team) && _knownTeams.Contains(team.Trim());

    public bool HasTeam(string? team, string? league)
    {
        if (string.IsNullOrWhiteSpace(team))
        {
            return false;
        }

        var model = ResolveLeagueModel(league, team, null);
        return model.HasTeam(team);
    }

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

    public MatchProbabilities Predict(string homeTeam, string awayTeam, string? league)
    {
        var model = ResolveLeagueModel(league, homeTeam, awayTeam);
        return ReferenceEquals(model, this)
            ? Predict(homeTeam, awayTeam)
            : model.Predict(homeTeam, awayTeam);
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
        var usable = NormalizeResults(results);
        var globalModel = FitSingle(usable, asOfUtc, options);
        var leagueModels = BuildLeagueModels(usable, asOfUtc, options, globalModel);
        if (leagueModels.Count == 0)
        {
            return globalModel;
        }

        return new DixonColesModel(
            options,
            globalModel.Intercept,
            globalModel.HomeAdvantage,
            globalModel.Rho,
            new Dictionary<string, double>(globalModel._attack, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, double>(globalModel._defence, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(globalModel._knownTeams, StringComparer.OrdinalIgnoreCase),
            leagueModels);
    }

    public static DixonColesHalfLifeTuningResult TuneHalfLife(
        IReadOnlyCollection<MatchResult> results,
        DateTime asOfUtc,
        DixonColesOptions? options = null)
    {
        options ??= new DixonColesOptions();
        var usable = NormalizeResults(results)
            .OrderBy(match => match.DateUtc)
            .ToList();
        if (usable.Count < Math.Max(40, options.MinMatchesPerTeam * 8))
        {
            return new DixonColesHalfLifeTuningResult(
                options.HalfLifeDays,
                options.HalfLifeDays,
                double.PositiveInfinity,
                double.PositiveInfinity,
                false);
        }

        var candidates = options.HalfLifeCandidateDays
            .Append(options.HalfLifeDays)
            .Where(days => days > 0)
            .Distinct()
            .OrderBy(days => days)
            .ToList();
        var minTrainingSize = Math.Max(24, usable.Count / 3);
        var bestHalfLife = options.HalfLifeDays;
        var bestBrier = double.PositiveInfinity;
        var incumbentBrier = double.PositiveInfinity;

        foreach (var halfLife in candidates)
        {
            var candidateOptions = options with { HalfLifeDays = halfLife };
            var report = Backtesting.WalkForwardBacktester.Run<MatchResult, DixonColesModel>(
                usable,
                match => match.DateUtc,
                training => Fit(training, training[^1].DateUtc, candidateOptions),
                (model, item) =>
                {
                    var probabilities = model.Predict(item.HomeTeam, item.AwayTeam, item.League);
                    var homeOutcome = item.HomeGoals > item.AwayGoals;
                    return new Backtesting.BacktestSample(probabilities.HomeWin, homeOutcome, DateUtc: item.DateUtc);
                },
                minTrainingSize,
                TimeSpan.FromDays(14));

            var brier = report.Metrics.Brier;
            if (Math.Abs(halfLife - options.HalfLifeDays) < 0.0001)
            {
                incumbentBrier = brier;
            }

            if (brier < bestBrier)
            {
                bestBrier = brier;
                bestHalfLife = halfLife;
            }
        }

        var promoted = bestHalfLife != options.HalfLifeDays &&
                       incumbentBrier - bestBrier >= options.HalfLifePromotionBrierTolerance;
        return new DixonColesHalfLifeTuningResult(
            promoted ? bestHalfLife : options.HalfLifeDays,
            options.HalfLifeDays,
            incumbentBrier,
            bestBrier,
            promoted);
    }

    private static DixonColesModel FitSingle(
        IReadOnlyCollection<MatchResult> results,
        DateTime asOfUtc,
        DixonColesOptions options)
    {
        var usable = NormalizeResults(results);

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

        var accumulatorAttack = teams.ToDictionary(team => team, _ => 0.0, StringComparer.OrdinalIgnoreCase);
        var accumulatorDefence = teams.ToDictionary(team => team, _ => 0.0, StringComparer.OrdinalIgnoreCase);
        var accumulatorIntercept = 0.0;
        var accumulatorHome = 0.0;
        var previousLogLikelihood = double.NegativeInfinity;

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

            var normalizedInterceptGradient = gradIntercept / Math.Max(totalWeight, 1e-9);
            var normalizedHomeGradient = gradHome / Math.Max(totalWeight, 1e-9);
            accumulatorIntercept += normalizedInterceptGradient * normalizedInterceptGradient;
            accumulatorHome += normalizedHomeGradient * normalizedHomeGradient;
            intercept += options.LearningRate * normalizedInterceptGradient / Math.Sqrt(accumulatorIntercept + 1e-8);
            homeAdvantage += options.LearningRate * normalizedHomeGradient / Math.Sqrt(accumulatorHome + 1e-8);
            homeAdvantage = Math.Clamp(homeAdvantage, 0.0, 1.0);

            foreach (var team in teams)
            {
                var normalizedAttackGradient = gradAttack[team] / Math.Max(totalWeight, 1e-9);
                var normalizedDefenceGradient = gradDefence[team] / Math.Max(totalWeight, 1e-9);
                accumulatorAttack[team] += normalizedAttackGradient * normalizedAttackGradient;
                accumulatorDefence[team] += normalizedDefenceGradient * normalizedDefenceGradient;
                attack[team] += options.LearningRate * normalizedAttackGradient / Math.Sqrt(accumulatorAttack[team] + 1e-8);
                defence[team] += options.LearningRate * normalizedDefenceGradient / Math.Sqrt(accumulatorDefence[team] + 1e-8);
            }

            // Identifiability: keep attack and defence centred on zero.
            var attackMean = attack.Values.Average();
            var defenceMean = defence.Values.Average();
            foreach (var team in teams)
            {
                attack[team] -= attackMean;
                defence[team] -= defenceMean;
            }

            var logLikelihood = WeightedPoissonLogLikelihood(usable, weights, intercept, homeAdvantage, attack, defence);
            if (iteration > 5 &&
                previousLogLikelihood > double.NegativeInfinity &&
                Math.Abs(logLikelihood - previousLogLikelihood) < options.ConvergenceTolerance)
            {
                break;
            }

            previousLogLikelihood = logLikelihood;
        }

        var rho = EstimateRho(usable, weights, intercept, homeAdvantage, attack, defence);

        return new DixonColesModel(options, intercept, homeAdvantage, rho, attack, defence, knownTeams);
    }

    private static Dictionary<string, DixonColesModel> BuildLeagueModels(
        IReadOnlyCollection<MatchResult> usable,
        DateTime asOfUtc,
        DixonColesOptions options,
        DixonColesModel globalModel)
    {
        var leagueModels = new Dictionary<string, DixonColesModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var leagueGroup in usable
                     .Where(match => !string.IsNullOrWhiteSpace(match.League))
                     .GroupBy(match => NormalizeLeagueKey(match.League), StringComparer.OrdinalIgnoreCase))
        {
            var leagueResults = leagueGroup.ToList();
            if (leagueResults.Count < options.MinMatchesPerLeague)
            {
                continue;
            }

            var leagueModel = FitSingle(leagueResults, asOfUtc, options);
            ShrinkLeagueModelTowardsGlobal(leagueModel, globalModel, leagueResults, options);
            leagueModels[leagueGroup.Key] = leagueModel;
        }

        return leagueModels;
    }

    private static void ShrinkLeagueModelTowardsGlobal(
        DixonColesModel leagueModel,
        DixonColesModel globalModel,
        IReadOnlyCollection<MatchResult> leagueResults,
        DixonColesOptions options)
    {
        var appearances = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in leagueResults)
        {
            appearances[match.HomeTeam] = appearances.GetValueOrDefault(match.HomeTeam) + 1;
            appearances[match.AwayTeam] = appearances.GetValueOrDefault(match.AwayTeam) + 1;
        }

        foreach (var team in leagueModel._attack.Keys.ToList())
        {
            var sampleCount = appearances.GetValueOrDefault(team);
            var leagueWeight = sampleCount / (double)(sampleCount + options.MinMatchesPerTeam);
            leagueModel._attack[team] = (leagueModel._attack[team] * leagueWeight) + (globalModel.GetAttack(team) * (1.0 - leagueWeight));
            leagueModel._defence[team] = (leagueModel._defence[team] * leagueWeight) + (globalModel.GetDefence(team) * (1.0 - leagueWeight));
        }
    }

    private DixonColesModel ResolveLeagueModel(string? league, string? homeTeam, string? awayTeam)
    {
        if (string.IsNullOrWhiteSpace(league))
        {
            return this;
        }

        var leagueKey = NormalizeLeagueKey(league);
        if (!_leagueModels.TryGetValue(leagueKey, out var leagueModel))
        {
            return this;
        }

        if (!string.IsNullOrWhiteSpace(homeTeam) && !leagueModel.HasTeam(homeTeam))
        {
            return this;
        }

        if (!string.IsNullOrWhiteSpace(awayTeam) && !leagueModel.HasTeam(awayTeam))
        {
            return this;
        }

        return leagueModel;
    }

    private static List<MatchResult> NormalizeResults(IReadOnlyCollection<MatchResult> results)
    {
        return results
            .Where(match => !string.IsNullOrWhiteSpace(match.HomeTeam) && !string.IsNullOrWhiteSpace(match.AwayTeam))
            .Select(match => new MatchResult(
                match.HomeTeam.Trim(),
                match.AwayTeam.Trim(),
                Math.Max(match.HomeGoals, 0),
                Math.Max(match.AwayGoals, 0),
                match.DateUtc,
                match.League?.Trim()))
            .ToList();
    }

    private static string NormalizeLeagueKey(string? league) =>
        string.IsNullOrWhiteSpace(league) ? string.Empty : league.Trim().ToLowerInvariant();

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

        for (var step = -40; step <= 40; step++)
        {
            var rho = step * 0.005;
            var logLikelihood = WeightedDixonColesTauLogLikelihood(matches, weights, intercept, homeAdvantage, attack, defence, rho);

            if (logLikelihood > bestLogLikelihood)
            {
                bestLogLikelihood = logLikelihood;
                bestRho = rho;
            }
        }

        var lower = Math.Max(-0.2, bestRho - 0.01);
        var upper = Math.Min(0.2, bestRho + 0.01);
        for (var rho = lower; rho <= upper + 0.000001; rho += 0.001)
        {
            var logLikelihood = WeightedDixonColesTauLogLikelihood(matches, weights, intercept, homeAdvantage, attack, defence, rho);
            if (logLikelihood > bestLogLikelihood)
            {
                bestLogLikelihood = logLikelihood;
                bestRho = Math.Round(rho, 4);
            }
        }

        return bestRho;
    }

    private static double WeightedPoissonLogLikelihood(
        IReadOnlyList<MatchResult> matches,
        IReadOnlyList<double> weights,
        double intercept,
        double homeAdvantage,
        IReadOnlyDictionary<string, double> attack,
        IReadOnlyDictionary<string, double> defence)
    {
        var logLikelihood = 0.0;
        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var lambda = ClampLambda(Math.Exp(intercept + homeAdvantage + attack[match.HomeTeam] - defence[match.AwayTeam]));
            var mu = ClampLambda(Math.Exp(intercept + attack[match.AwayTeam] - defence[match.HomeTeam]));
            logLikelihood += weights[i] * ((match.HomeGoals * Math.Log(lambda)) - lambda + (match.AwayGoals * Math.Log(mu)) - mu);
        }

        return logLikelihood;
    }

    private static double WeightedDixonColesTauLogLikelihood(
        IReadOnlyList<MatchResult> matches,
        IReadOnlyList<double> weights,
        double intercept,
        double homeAdvantage,
        IReadOnlyDictionary<string, double> attack,
        IReadOnlyDictionary<string, double> defence,
        double rho)
    {
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

        return logLikelihood;
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
