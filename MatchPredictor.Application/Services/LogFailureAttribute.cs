using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.Logging;

namespace MatchPredictor.Application.Services;

public class LogFailureAttribute : JobFilterAttribute, IApplyStateFilter
{
    private readonly ILogger<LogFailureAttribute> _logger;

    public LogFailureAttribute(ILogger<LogFailureAttribute> logger)
    {
        _logger = logger;
    }

    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        if (context.NewState is FailedState failed)
        {
            _logger.LogError(failed.Exception, 
                "Job {JobId} failed", 
                context.BackgroundJob.Id);
        }
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction) { }
}