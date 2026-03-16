namespace MatchPredictor.Web.Services;

public sealed class OperationalStartupState
{
    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
    public bool DatabaseInitialized { get; private set; }
    public bool HangfireInitialized { get; private set; }
    public bool RecurringJobsRegistered { get; private set; }
    public string? InitializationError { get; private set; }

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

    public void MarkInitializationFailed(string message)
    {
        InitializationError = message;
    }
}
