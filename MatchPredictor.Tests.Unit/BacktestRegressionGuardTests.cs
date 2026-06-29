using MatchPredictor.Infrastructure.Statistics;
using MatchPredictor.Infrastructure.Statistics.Backtesting;
using Xunit;

namespace MatchPredictor.Tests.Unit;

/// <summary>
/// Regression gate for the statistical core. Using a fully deterministic synthetic season
/// (seeded RNG, known data-generating process), it asserts that the Dixon-Coles + market
/// ensemble keeps its out-of-sample quality and profitability above locked thresholds. If a
/// future model change degrades Brier or ROI beyond tolerance, these tests fail.
/// </summary>
public class BacktestRegressionGuardTests
{
    private const int Seed = 20260629;
    private const int TeamCount = 14;
    private const int Rounds = 3; // double round-robin repeated -> plenty of fixtures
    private const double BaseRate = 0.10;
    private const double HomeAdvantage = 0.25;

    [Fact]
    public void DixonColesEnsemble_OverTwoFive_StaysAboveLockedQualityThresholds()
    {
        var (train, test) = BuildSyntheticSeason();
        var model = DixonColesModel.Fit(train.Select(m => m.Result).ToList(), test[0].Result.DateUtc);

        var ensembleSamples = new List<BacktestSample>();
        var marketSamples = new List<BacktestSample>();

        foreach (var fixture in test)
        {
            if (!model.HasTeam(fixture.Result.HomeTeam) || !model.HasTeam(fixture.Result.AwayTeam))
            {
                continue;
            }

            var dc = model.Predict(fixture.Result.HomeTeam, fixture.Result.AwayTeam);

            // Market signal = the true probability blurred with deterministic noise.
            var market = Math.Clamp(fixture.TrueOver25 + fixture.MarketNoise, 0.05, 0.95);
            var ensemble = EnsembleProbabilityBlender.BlendLogit((market, 1.0), (dc.Over25, 1.1));

            var outcome = fixture.Result.HomeGoals + fixture.Result.AwayGoals > 2.5;
            var odds = 1.0 / Math.Clamp(market, 0.05, 0.95) * 1.05; // 5% margin priced off the market view

            ensembleSamples.Add(new BacktestSample(ensemble, outcome, DecimalOdds: odds, DateUtc: fixture.Result.DateUtc));
            marketSamples.Add(new BacktestSample(market, outcome, DecimalOdds: odds, DateUtc: fixture.Result.DateUtc));
        }

        var ensembleMetrics = BacktestEvaluator.Evaluate(ensembleSamples);
        var marketMetrics = BacktestEvaluator.Evaluate(marketSamples);

        Assert.True(ensembleSamples.Count >= 50, $"Too few out-of-sample fixtures: {ensembleSamples.Count}");

        // Locked absolute quality ceilings (regression gate).
        Assert.True(ensembleMetrics.Brier < 0.30, $"Ensemble Brier regressed: {ensembleMetrics.Brier:F4}");
        Assert.True(ensembleMetrics.Ece < 0.15, $"Ensemble ECE regressed: {ensembleMetrics.Ece:F4}");
        Assert.True(ensembleMetrics.Accuracy > 0.50, $"Ensemble accuracy regressed: {ensembleMetrics.Accuracy:F4}");

        // The ensemble must not be materially worse than the market-only baseline.
        Assert.True(
            ensembleMetrics.Brier <= marketMetrics.Brier + 0.01,
            $"Ensemble Brier {ensembleMetrics.Brier:F4} regressed vs market {marketMetrics.Brier:F4}");
    }

    [Fact]
    public void DixonColesEnsemble_DoesNotPostCatastrophicNegativeRoi()
    {
        var (train, test) = BuildSyntheticSeason();
        var model = DixonColesModel.Fit(train.Select(m => m.Result).ToList(), test[0].Result.DateUtc);

        var samples = new List<BacktestSample>();
        foreach (var fixture in test)
        {
            if (!model.HasTeam(fixture.Result.HomeTeam) || !model.HasTeam(fixture.Result.AwayTeam))
            {
                continue;
            }

            var dc = model.Predict(fixture.Result.HomeTeam, fixture.Result.AwayTeam);
            var market = Math.Clamp(fixture.TrueOver25 + fixture.MarketNoise, 0.05, 0.95);
            var ensemble = EnsembleProbabilityBlender.BlendLogit((market, 1.0), (dc.Over25, 1.1));
            var outcome = fixture.Result.HomeGoals + fixture.Result.AwayGoals > 2.5;
            // Fair odds off the TRUE probability (no margin) — a realistic, beatable book.
            var odds = 1.0 / Math.Clamp(fixture.TrueOver25, 0.05, 0.95);
            samples.Add(new BacktestSample(ensemble, outcome, DecimalOdds: odds, DateUtc: fixture.Result.DateUtc));
        }

        var metrics = BacktestEvaluator.Evaluate(samples, betThreshold: 0.55);

        // Against fair (zero-margin) odds, a well-calibrated model should not bleed badly.
        Assert.True(metrics.Roi > -0.15, $"Ensemble ROI regressed: {metrics.Roi:F4}");
    }

    private static (IReadOnlyList<Fixture> Train, IReadOnlyList<Fixture> Test) BuildSyntheticSeason()
    {
        var rng = new Random(Seed);
        var teams = Enumerable.Range(0, TeamCount).Select(i => $"Team{i:D2}").ToList();

        var attack = teams.ToDictionary(team => team, _ => NextGaussian(rng) * 0.35);
        var defence = teams.ToDictionary(team => team, _ => NextGaussian(rng) * 0.35);

        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var fixtures = new List<Fixture>();
        var day = 0;

        for (var round = 0; round < Rounds; round++)
        {
            foreach (var home in teams)
            {
                foreach (var away in teams)
                {
                    if (home == away)
                    {
                        continue;
                    }

                    var lambdaHome = Math.Exp(BaseRate + attack[home] - defence[away] + HomeAdvantage);
                    var lambdaAway = Math.Exp(BaseRate + attack[away] - defence[home]);

                    var homeGoals = SamplePoisson(rng, lambdaHome);
                    var awayGoals = SamplePoisson(rng, lambdaAway);

                    var trueOver25 = TrueOverProbability(lambdaHome + lambdaAway);
                    var marketNoise = (NextGaussian(rng)) * 0.04;

                    fixtures.Add(new Fixture(
                        new MatchResult(home, away, homeGoals, awayGoals, start.AddHours(day)),
                        trueOver25,
                        marketNoise));
                    day++;
                }
            }
        }

        var splitIndex = (int)(fixtures.Count * 0.6);
        return (fixtures.Take(splitIndex).ToList(), fixtures.Skip(splitIndex).ToList());
    }

    private static double TrueOverProbability(double lambdaTotal)
    {
        // P(total goals > 2.5) = 1 - P(0) - P(1) - P(2) for total ~ Poisson(lambdaTotal).
        var p0 = Math.Exp(-lambdaTotal);
        var p1 = p0 * lambdaTotal;
        var p2 = p1 * lambdaTotal / 2.0;
        return Math.Clamp(1.0 - (p0 + p1 + p2), 0.0, 1.0);
    }

    private static int SamplePoisson(Random rng, double lambda)
    {
        // Knuth's algorithm — deterministic for a seeded RNG.
        var l = Math.Exp(-lambda);
        var k = 0;
        var p = 1.0;
        do
        {
            k++;
            p *= rng.NextDouble();
        }
        while (p > l);

        return k - 1;
    }

    private static double NextGaussian(Random rng)
    {
        // Box-Muller transform.
        var u1 = 1.0 - rng.NextDouble();
        var u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private sealed record Fixture(MatchResult Result, double TrueOver25, double MarketNoise);
}
