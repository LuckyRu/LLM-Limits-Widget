namespace LLMLimitsWidget.FloatingOverlay;

/// <summary>
/// Temporary adapters that keep the visual prototype alive while real Codex and
/// Claude transports are implemented. They already use the production domain
/// contract, so replacing them does not change the widget or coordinator.
/// </summary>
public sealed class DemoCodexLimitsDataSource : ILimitsDataSource
{
    public LimitProviderId Provider => LimitProviderId.Codex;

    public Task<ProviderLimitsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.Now;
        return Task.FromResult(new ProviderLimitsSnapshot(
            Provider,
            now,
            new[]
            {
                new LimitWindowSnapshot(
                    LimitWindowKind.Weekly,
                    "W",
                    62.00,
                    NextLocalReset(now, days: 3, hours: 7, minutes: 3))
            }));
    }

    private static DateTimeOffset NextLocalReset(
        DateTimeOffset now,
        int days,
        int hours,
        int minutes) => now.Date.AddDays(days).AddHours(hours).AddMinutes(minutes);
}

public sealed class DemoClaudeLimitsDataSource : ILimitsDataSource
{
    public LimitProviderId Provider => LimitProviderId.Claude;

    public Task<ProviderLimitsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.Now;
        return Task.FromResult(new ProviderLimitsSnapshot(
            Provider,
            now,
            new[]
            {
                new LimitWindowSnapshot(
                    LimitWindowKind.FiveHour,
                    "5h",
                    5.00,
                    now.AddMinutes(30)),
                new LimitWindowSnapshot(
                    LimitWindowKind.SevenDay,
                    "7d",
                    70.00,
                    now.AddDays(1).AddMinutes(30))
            }));
    }
}
