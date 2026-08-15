namespace LLMLimitsWidget.Domain;

/// <summary>
/// Provider-neutral scheduling policy owned by the domain. Transports report
/// typed retry dispositions; only this policy decides when another attempt is
/// allowed to be scheduled.
/// </summary>
public static class ProviderRefreshSchedule
{
    private static readonly TimeSpan[] BackoffSteps =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(45),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15)
    ];

    public static TimeSpan HealthyInterval(ProviderId provider) => provider switch
    {
        ProviderId.Codex => TimeSpan.FromMinutes(2),
        ProviderId.Claude => TimeSpan.FromMinutes(5),
        _ => TimeSpan.FromMinutes(5)
    };

    /// <summary>
    /// A fresh Claude Code statusLine is a low-cost push signal. Keep direct
    /// CLI as a reconciliation/fallback path, not a second five-minute poll
    /// while the interactive session is actively providing observations.
    /// </summary>
    public static TimeSpan StatusLineReconciliationInterval(ProviderId provider) => provider switch
    {
        ProviderId.Claude => TimeSpan.FromMinutes(15),
        _ => HealthyInterval(provider)
    };

    public static TimeSpan RetryDelay(RetryDisposition retry, int consecutiveFailures) => retry switch
    {
        RetryDisposition.Immediate => TimeSpan.FromSeconds(1),
        RetryDisposition.Backoff => BackoffSteps[Math.Clamp(consecutiveFailures - 1, 0, BackoffSteps.Length - 1)],
        _ => Timeout.InfiniteTimeSpan
    };
}
