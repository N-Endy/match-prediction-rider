using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MatchPredictor.Infrastructure.Services;

public sealed class MlXgFeatureReadiness : IMlXgFeatureReadiness
{
    private readonly ApplicationDbContext _dbContext;

    public MlXgFeatureReadiness(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<MlXgFeatureReadinessResult> EvaluateAsync(CancellationToken cancellationToken = default)
    {
        var sampleSize = await _dbContext.TeamMatchStats.CountAsync(cancellationToken);
        var withXg = await _dbContext.TeamMatchStats
            .CountAsync(row => row.ExpectedGoalsFor != null, cancellationToken);
        var coverage = sampleSize == 0 ? 0.0 : withXg / (double)sampleSize;
        var ready = sampleSize >= 50 && coverage >= IMlXgFeatureReadiness.MinimumXgCoverage;

        return new MlXgFeatureReadinessResult
        {
            XgCoverage = coverage,
            SampleSize = sampleSize,
            WithXg = withXg,
            MayBumpFeatureSchema = ready,
            Reason = ready
                ? "Coverage clears the 70% gate — FeatureColumns may add Home/AwayExpectedGoalsFor and HasExpectedGoals, then force a model rebuild."
                : "Do not change MarketPredictionModelService.FeatureColumns yet. SofaScore has no stored xG; wire a licensed current-season source into TeamMatchStats.ExpectedGoals* first."
        };
    }
}
