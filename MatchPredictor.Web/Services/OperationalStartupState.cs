namespace MatchPredictor.Web.Services;

public sealed class OperationalStartupState
{
    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
    public bool BackgroundJobsEnabled { get; private set; } = true;
    public bool BrowserScrapingEnabled { get; private set; } = true;
    public bool DatabaseInitialized { get; private set; }
    public bool HangfireInitialized { get; private set; }
    public bool RecurringJobsRegistered { get; private set; }
    public bool ExternalCronEnabled { get; private set; }
    public string? InitializationError { get; private set; }

    public void ConfigureRuntimeMode(bool backgroundJobsEnabled, bool browserScrapingEnabled, bool externalCronEnabled)
    {
        BackgroundJobsEnabled = backgroundJobsEnabled;
        BrowserScrapingEnabled = browserScrapingEnabled;
        ExternalCronEnabled = externalCronEnabled;
    }

    public void MarkDatabaseInitialized()
    {
        DatabaseInitialized = true;
        InitializationError = null;
    }

    public void MarkHangfireInitialized()
    {
        HangfireInitialized = true;
    }

    public void MarkRecurringJobsRegistered()
    {
        RecurringJobsRegistered = true;
    }

    public void MarkExternalCronEnabled()
    {
        ExternalCronEnabled = true;
        RecurringJobsRegistered = false;
    }

    public void MarkInitializationFailed(string message)
    {
        InitializationError = message;
    }
}
