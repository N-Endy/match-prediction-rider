using MatchPredictor.Application.Helpers;
using MatchPredictor.Domain.Models;
using MatchPredictor.Infrastructure.Services;
using MatchPredictor.Infrastructure.Statistics;
using Xunit;

namespace MatchPredictor.Tests.Unit;

public class MathPipelineAuditTests
{
    // =========================================================================
    // LAYER 1: Probabilistic Axioms & Simplex Normalization
    // =========================================================================

    [Fact]
    public void SimplexNormalization_1X2StrictlySumsToUnity_PostCalibration()
    {
        // Skewed post-calibration independent probabilities summing to 1.30
        var candidates = new List<PredictionCandidate>
        {
            new()
            {
                Market = PredictionMarket.HomeWin,
                CalibratedProbability = 0.55,
                CorrectedProbability = 0.50
            },
            new()
            {
                Market = PredictionMarket.Draw,
                CalibratedProbability = 0.35,
                CorrectedProbability = 0.30
            },
            new()
            {
                Market = PredictionMarket.AwayWin,
                CalibratedProbability = 0.40,
                CorrectedProbability = 0.40
            }
        };

        DataAnalyzerService.NormalizeSimplexProbabilities(candidates);

        var home = candidates.First(c => c.Market == PredictionMarket.HomeWin);
        var draw = candidates.First(c => c.Market == PredictionMarket.Draw);
        var away = candidates.First(c => c.Market == PredictionMarket.AwayWin);

        var calibratedSum = home.CalibratedProbability + draw.CalibratedProbability + away.CalibratedProbability;
        var correctedSum = home.CorrectedProbability + draw.CorrectedProbability + away.CorrectedProbability;

        Assert.Equal(1.0, calibratedSum, precision: 9);
        Assert.Equal(1.0, correctedSum, precision: 9);

        Assert.InRange(home.CalibratedProbability, 0.0, 1.0);
        Assert.InRange(draw.CalibratedProbability, 0.0, 1.0);
        Assert.InRange(away.CalibratedProbability, 0.0, 1.0);
    }

    [Fact]
    public void SimplexNormalization_ComplementaryCoherence_UnderOver25()
    {
        var candidates = new List<PredictionCandidate>
        {
            new()
            {
                Market = PredictionMarket.Over25Goals,
                CalibratedProbability = 0.65,
                CorrectedProbability = 0.60
            },
            new()
            {
                Market = PredictionMarket.Under25Goals,
                CalibratedProbability = 0.45, // Incoherent initial sum 1.10
                CorrectedProbability = 0.50
            }
        };

        DataAnalyzerService.NormalizeSimplexProbabilities(candidates);

        var over = candidates.First(c => c.Market == PredictionMarket.Over25Goals);
        var under = candidates.First(c => c.Market == PredictionMarket.Under25Goals);

        Assert.Equal(1.0 - over.CalibratedProbability, under.CalibratedProbability, precision: 9);
        Assert.Equal(1.0 - over.CorrectedProbability, under.CorrectedProbability, precision: 9);
    }

    [Fact]
    public void PredictionScoreClassHelper_LiveInPlayUnder25_ResolvesFalse()
    {
        // Live match currently 0-0 at minute 50: Under 2.5 has not yet settled, must not be marked correct prematurely
        var predictionLive = new Prediction
        {
            IsLive = true,
            PredictionCategory = "Under2.5Goals",
            PredictedOutcome = "Under 2.5",
            ActualScore = "0-0"
        };
        var isCorrect = PredictionScoreClassHelper.IsLivePredictionCorrect(predictionLive);
        Assert.False(isCorrect);

        // Finished match: settled score evaluates accurately
        var predictionFinished = new Prediction
        {
            IsLive = false,
            PredictionCategory = "Under2.5Goals",
            PredictedOutcome = "Under 2.5",
            ActualScore = "1-1"
        };
        var finishedCorrect = PredictionScoreClassHelper.IsLivePredictionCorrect(predictionFinished);
        Assert.True(finishedCorrect);
    }

    // =========================================================================
    // LAYER 2: Statistical Models & Sourcing
    // =========================================================================

    [Fact]
    public void EloRating_ExtremeGap_GuaranteesValidSimplexWithoutNegativeProbabilities()
    {
        var model = new EloRatingModel();
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var results = new List<MatchResult>();
        for (var i = 0; i < 50; i++)
        {
            results.Add(new MatchResult("Strong", "Weak", 5, 0, start.AddDays(i)));
        }
        model.Train(results);

        var (homeWin, draw, awayWin) = model.PredictResult("Strong", "Weak");

        Assert.True(homeWin >= 0.0 && homeWin <= 1.0, $"HomeWin {homeWin} out of range");
        Assert.True(draw >= 0.0 && draw <= 1.0, $"Draw {draw} out of range");
        Assert.True(awayWin >= 0.0 && awayWin <= 1.0, $"AwayWin {awayWin} out of range");

        var sum = homeWin + draw + awayWin;
        Assert.Equal(1.0, sum, precision: 6);
    }

    [Fact]
    public void ShinDeVig_HighOverround_BisectionExpandsAndConverges()
    {
        // High margin market: [1.30, 2.80, 4.00] -> overround ~ 1.376
        var odds = new double[] { 1.30, 2.80, 4.00 };
        var fair = OddsMath.FairProbabilitiesShin(odds);

        Assert.Equal(3, fair.Length);
        var sum = fair.Sum();
        Assert.Equal(1.0, sum, precision: 4);

        foreach (var p in fair)
        {
            Assert.True(p > 0.0 && p < 1.0);
        }
    }

    // =========================================================================
    // LAYER 3: Calibration & Parameter Optimization
    // =========================================================================

    [Fact]
    public void BetaCalibration_GradientDescent_ConvergesAndPreservesMonotonicity()
    {
        // Generate training data with slight overconfidence
        var training = new List<(double RawProbability, bool Outcome, double Weight)>();
        for (var i = 1; i <= 50; i++)
        {
            var pRaw = i / 51.0;
            var outcome = pRaw > 0.6; // step-like true outcome
            training.Add((pRaw, outcome, 1.0));
        }

        var (alpha, beta, gamma) = CalibrationService.FitBetaCalibration(training);

        Assert.True(alpha > 0, "Alpha must be positive for monotonicity");
        Assert.True(beta > 0, "Beta must be positive for monotonicity");

        // Verify monotonicity across test points
        var p1 = CalibrationService.ApplyBetaCalibration(0.2, alpha, beta, gamma);
        var p2 = CalibrationService.ApplyBetaCalibration(0.5, alpha, beta, gamma);
        var p3 = CalibrationService.ApplyBetaCalibration(0.8, alpha, beta, gamma);

        Assert.True(p1 <= p2, "Calibration must be non-decreasing (p1 <= p2)");
        Assert.True(p2 <= p3, "Calibration must be non-decreasing (p2 <= p3)");
    }

    [Fact]
    public void IsotonicRegression_PreservesPlateausWithoutLinearCollapse()
    {
        var observations = new List<(double Input, bool Outcome, double Weight)>
        {
            (0.1, false, 1.0),
            (0.2, false, 1.0),
            (0.5, true, 1.0),
            (0.6, true, 1.0),
            (0.7, true, 1.0),
            (0.8, true, 1.0),
            (0.9, true, 1.0)
        };

        var knots = IsotonicRegression.Fit(observations);

        Assert.NotEmpty(knots);
        for (var i = 1; i < knots.Count; i++)
        {
            Assert.True(knots[i].Output >= knots[i - 1].Output);
            Assert.True(knots[i].Input >= knots[i - 1].Input);
        }
    }

    // =========================================================================
    // LAYER 4: Value Betting & Kelly Sizing
    // =========================================================================

    [Theory]
    [InlineData(0.60, 2.00, 0.25, 0.05)] // Edge = 0.6*1 - 0.4 = 0.2, full = 0.2, quarter = 0.05
    [InlineData(0.50, 2.00, 0.25, 0.00)] // Fair odds, 0 edge -> 0 stake
    [InlineData(0.40, 2.00, 0.25, 0.00)] // Negative edge -> 0 stake
    public void BetPricingMath_FractionalKelly_CalculatesCorrectStake(
        double prob,
        double odds,
        double fraction,
        double expectedStake)
    {
        var stake = BetPricingMath.CalculateFractionalKellyStakeFraction(prob, odds, fraction);
        Assert.Equal(expectedStake, stake, precision: 4);
    }

    // =========================================================================
    // LAYER 5: Accumulator Branch-and-Bound
    // =========================================================================

    [Fact]
    public void WeekendPayoutSlipComposer_BranchAndBound_PacksWhenGreedyDeadEnds()
    {
        // Construct candidates where first candidate is huge and blocks remaining greedy picks,
        // but skipping it allows branch-and-bound to find a valid combination.
        var candidates = new List<BetslipComposerCandidate>
        {
            new()
            {
                PredictionId = 1,
                FixtureKey = "fix-1",
                League = "L1",
                HomeTeam = "H1",
                AwayTeam = "A1",
                Market = "StraightWin",
                PredictionCategory = "StraightWin",
                Confidence = 0.99m,
                DecimalOdds = 80.0, // High odds that would overshoot maxOdds if paired with anything
                MatchDateTimeUtc = DateTime.UtcNow.AddHours(1)
            },
            new()
            {
                PredictionId = 2,
                FixtureKey = "fix-2",
                League = "L2",
                HomeTeam = "H2",
                AwayTeam = "A2",
                Market = "BTTS",
                PredictionCategory = "BothTeamsScore",
                Confidence = 0.95m,
                DecimalOdds = 6.0,
                MatchDateTimeUtc = DateTime.UtcNow.AddHours(2)
            },
            new()
            {
                PredictionId = 3,
                FixtureKey = "fix-3",
                League = "L3",
                HomeTeam = "H3",
                AwayTeam = "A3",
                Market = "Over2.5",
                PredictionCategory = "Over2.5Goals",
                Confidence = 0.90m,
                DecimalOdds = 7.0,
                MatchDateTimeUtc = DateTime.UtcNow.AddHours(3)
            }
        };

        var spec = new PayoutBandSpec
        {
            SlipNumber = 1,
            Title = "Test Acca",
            BandKey = "small",
            MinOdds = 35.0,
            MaxOdds = 50.0,
            FallbackMinOdds = 20.0,
            FallbackMaxOdds = 80.0,
            MaxPicks = 3
        };

        var slips = WeekendPayoutSlipComposer.Compose(candidates, [spec]);

        var slip = Assert.Single(slips);
        // Candidate 2 (6.0) * Candidate 3 (7.0) = 42.0, perfectly in [35.0, 50.0]
        Assert.InRange(slip.TargetCombinedOdds!.Value, 35.0, 50.0);
        Assert.Equal(2, slip.Selections.Count);
        Assert.Contains(slip.Selections, s => s.PredictionId == 2);
        Assert.Contains(slip.Selections, s => s.PredictionId == 3);
    }

    // =========================================================================
    // LAYER 6: Evaluation & Analytics Metrics Alignment
    // =========================================================================

    [Fact]
    public void ForecastEvaluation_TrueConfusionMatrix_ComputesDistinctRecallWhenFalseNegativesExist()
    {
        var service = new ForecastEvaluationService();

        // 2 published predictions: 1 correct (TP), 1 incorrect (FP)
        var predictions = new List<Prediction>
        {
            new()
            {
                Id = 1,
                FixtureKey = "fix-1",
                PredictionCategory = "BothTeamsScore",
                PredictedOutcome = "BTTS",
                ActualScore = "1-1", // Correct (TP)
                WasPublished = true,
                ConfidenceScore = 0.70m
            },
            new()
            {
                Id = 2,
                FixtureKey = "fix-2",
                PredictionCategory = "BothTeamsScore",
                PredictedOutcome = "BTTS",
                ActualScore = "1-0", // Incorrect (FP)
                WasPublished = true,
                ConfidenceScore = 0.65m
            }
        };

        // 3 settled forecasts:
        // - Forecast 1: published, outcome occurred (TP)
        // - Forecast 2: published, outcome did not occur (FP)
        // - Forecast 3: NOT published, outcome DID occur (False Negative FN!)
        var forecasts = new List<ForecastObservation>
        {
            new()
            {
                Id = 1,
                FixtureKey = "fix-1",
                Market = PredictionMarket.BothTeamsScore,
                PredictedOutcome = "BTTS",
                IsPublished = true,
                IsSettled = true,
                OutcomeOccurred = true,
                RawProbability = 0.70,
                CalibratedProbability = 0.70
            },
            new()
            {
                Id = 2,
                FixtureKey = "fix-2",
                Market = PredictionMarket.BothTeamsScore,
                PredictedOutcome = "BTTS",
                IsPublished = true,
                IsSettled = true,
                OutcomeOccurred = false,
                RawProbability = 0.65,
                CalibratedProbability = 0.65
            },
            new()
            {
                Id = 3,
                FixtureKey = "fix-3",
                Market = PredictionMarket.BothTeamsScore,
                PredictedOutcome = "BTTS",
                IsPublished = false, // Not published
                IsSettled = true,
                OutcomeOccurred = true, // But outcome occurred -> False Negative!
                RawProbability = 0.40,
                CalibratedProbability = 0.40
            }
        };

        var stats = service.CalculateStats(predictions, forecasts);

        // Accuracy = TP / TotalPublished = 1 / 2 = 0.50
        Assert.Equal(0.50, stats.OverallAccuracy, precision: 4);

        // Precision = TP / (TP + FP) = 1 / 2 = 0.50
        Assert.Equal(0.50, stats.Precision, precision: 4);

        // Recall = TP / (TP + FN) = 1 / (1 + 1) = 1 / 2 = 0.50
        // Now if we have 2 FN:
        var forecastsWith2Fn = new List<ForecastObservation>(forecasts)
        {
            new()
            {
                Id = 4,
                FixtureKey = "fix-4",
                Market = PredictionMarket.BothTeamsScore,
                PredictedOutcome = "BTTS",
                IsPublished = false,
                IsSettled = true,
                OutcomeOccurred = true, // Second False Negative!
                RawProbability = 0.35,
                CalibratedProbability = 0.35
            }
        };

        var statsWith2Fn = service.CalculateStats(predictions, forecastsWith2Fn);
        // Accuracy stays 1 / 2 = 0.50
        Assert.Equal(0.50, statsWith2Fn.OverallAccuracy, precision: 4);
        // Precision stays 1 / 2 = 0.50
        Assert.Equal(0.50, statsWith2Fn.Precision, precision: 4);
        // Recall = TP / (TP + FN) = 1 / (1 + 2) = 1 / 3 = 0.3333 != Accuracy!
        Assert.Equal(1.0 / 3.0, statsWith2Fn.Recall, precision: 4);
        Assert.NotEqual(statsWith2Fn.OverallAccuracy, statsWith2Fn.Recall);
    }

    [Fact]
    public void ForecastEvaluation_DrawMarket_ReintegratedIntoActiveAnalytics()
    {
        var service = new ForecastEvaluationService();

        var predictions = new List<Prediction>
        {
            new()
            {
                Id = 1,
                FixtureKey = "fix-draw",
                PredictionCategory = "Draw",
                PredictedOutcome = "Draw",
                ActualScore = "1-1",
                WasPublished = true,
                ConfidenceScore = 0.33m
            }
        };

        var forecasts = new List<ForecastObservation>
        {
            new()
            {
                Id = 1,
                FixtureKey = "fix-draw",
                Market = PredictionMarket.Draw,
                PredictedOutcome = "Draw",
                IsPublished = true,
                IsSettled = true,
                OutcomeOccurred = true,
                RawProbability = 0.33,
                CalibratedProbability = 0.33
            }
        };

        var stats = service.CalculateStats(predictions, forecasts);

        Assert.Equal(1, stats.CompletedPredictions);
        Assert.True(stats.CategoryStats.ContainsKey("Draw"), "Draw category must be present in CategoryStats");
        Assert.Equal(1.0, stats.CategoryStats["Draw"].Accuracy);
    }
}
