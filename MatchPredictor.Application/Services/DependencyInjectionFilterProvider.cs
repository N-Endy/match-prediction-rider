using System;
using System.Collections.Generic;
using Hangfire.Common;
using Microsoft.Extensions.DependencyInjection;

namespace MatchPredictor.Application.Services;

public class DependencyInjectionFilterProvider : IJobFilterProvider
{
    private readonly IServiceProvider _serviceProvider;

    public DependencyInjectionFilterProvider(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public IEnumerable<JobFilter> GetFilters(Job job)
    {
        yield return new JobFilter(
            _serviceProvider.GetRequiredService<LogFailureAttribute>(),
            JobFilterScope.Global,
            null
        );
    }
}