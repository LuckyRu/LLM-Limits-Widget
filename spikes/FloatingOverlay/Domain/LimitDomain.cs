using System.Collections.ObjectModel;

namespace LLMLimitsWidget.FloatingOverlay;

public enum LimitProviderId
{
    Codex,
    Claude
}

public enum LimitWindowKind
{
    Weekly,
    FiveHour,
    SevenDay
}

public enum LimitDataStatus
{
    Fresh,
    Stale,
    Unavailable,
    Error
}

/// <summary>
/// Provider-neutral representation of one subscription limit window.
/// Percentages are remaining percentages in the inclusive range 0..100.
/// </summary>
public sealed record LimitWindowSnapshot(
    LimitWindowKind Kind,
    string Label,
    double? RemainingPercent,
    DateTimeOffset? ResetAt,
    LimitDataStatus Status = LimitDataStatus.Fresh,
    string? ErrorMessage = null)
{
    public double? SafeRemainingPercent => RemainingPercent is { } value
        ? Math.Clamp(value, 0, 100)
        : null;
}

public sealed record ProviderLimitsSnapshot(
    LimitProviderId Provider,
    DateTimeOffset ObservedAt,
    IReadOnlyList<LimitWindowSnapshot> Windows,
    LimitDataStatus Status = LimitDataStatus.Fresh,
    string? ErrorMessage = null)
{
    public static ProviderLimitsSnapshot Unavailable(
        LimitProviderId provider,
        DateTimeOffset observedAt,
        string message) => new(
            provider,
            observedAt,
            Array.Empty<LimitWindowSnapshot>(),
            LimitDataStatus.Unavailable,
            message);
}

public sealed record LimitsSnapshot(
    DateTimeOffset UpdatedAt,
    IReadOnlyDictionary<LimitProviderId, ProviderLimitsSnapshot> Providers)
{
    public static LimitsSnapshot Empty(DateTimeOffset now) => new(
        now,
        new ReadOnlyDictionary<LimitProviderId, ProviderLimitsSnapshot>(
            new Dictionary<LimitProviderId, ProviderLimitsSnapshot>()));

    public bool TryGetProvider(
        LimitProviderId provider,
        out ProviderLimitsSnapshot snapshot) => Providers.TryGetValue(provider, out snapshot!);
}

public interface ILimitsDataSource
{
    LimitProviderId Provider { get; }

    Task<ProviderLimitsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

public interface IForceRefreshableLimitsDataSource : ILimitsDataSource
{
    Task<ProviderLimitsSnapshot> ForceRefreshAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Coordinates independent provider sources. One failing provider never prevents
/// the other provider from updating the widget.
/// </summary>
public sealed class LimitsCoordinator : IAsyncDisposable
{
    private readonly IReadOnlyList<ILimitsDataSource> _sources;
    private readonly TimeSpan _refreshInterval;
    private readonly object _sync = new();
    private CancellationTokenSource? _lifetime;
    private Task? _refreshLoop;
    private LimitsSnapshot _current = LimitsSnapshot.Empty(DateTimeOffset.UtcNow);

    public LimitsCoordinator(
        IEnumerable<ILimitsDataSource> sources,
        TimeSpan? refreshInterval = null)
    {
        _sources = sources
            .GroupBy(source => source.Provider)
            .Select(group => group.First())
            .ToArray();
        _refreshInterval = refreshInterval ?? TimeSpan.FromSeconds(30);

        if (_refreshInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(refreshInterval));
        }
    }

    public event Action<LimitsSnapshot>? SnapshotChanged;

    public LimitsSnapshot Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_refreshLoop is not null)
            {
                return;
            }

            _lifetime = new CancellationTokenSource();
            _refreshLoop = RefreshLoopAsync(_lifetime.Token);
        }

        WidgetLogger.Info(
            "Limits",
            "refresh_loop_started",
            ("providerCount", _sources.Count),
            ("intervalSeconds", _refreshInterval.TotalSeconds));
    }

    public async Task<LimitsSnapshot> RefreshAsync(
        CancellationToken cancellationToken = default,
        bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        var previous = Current;
        WidgetLogger.Debug("Limits", "refresh_started", ("force", force));
        var results = await Task.WhenAll(_sources.Select(source =>
            ReadSourceSafelyAsync(source, previous, now, cancellationToken, force)));
        var providers = results.ToDictionary(result => result.Provider);
        var snapshot = new LimitsSnapshot(
            now,
            new ReadOnlyDictionary<LimitProviderId, ProviderLimitsSnapshot>(providers));

        lock (_sync)
        {
            _current = snapshot;
        }

        try
        {
            SnapshotChanged?.Invoke(snapshot);
        }
        catch (Exception exception)
        {
            WidgetLogger.Error("Limits", "snapshot_consumer_failed", exception);
        }

        WidgetLogger.Debug(
            "Limits",
            "refresh_completed",
            ("providerCount", providers.Count),
            ("windowCount", providers.Values.Sum(provider => provider.Windows.Count)));
        return snapshot;
    }

    public async ValueTask DisposeAsync()
    {
        Task? refreshLoop;
        CancellationTokenSource? lifetime;
        lock (_sync)
        {
            refreshLoop = _refreshLoop;
            lifetime = _lifetime;
            _refreshLoop = null;
            _lifetime = null;
        }

        if (lifetime is null)
        {
            return;
        }

        lifetime.Cancel();
        try
        {
            if (refreshLoop is not null)
            {
                await refreshLoop.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_refreshInterval);
        try
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            WidgetLogger.Critical("Limits", "refresh_loop_failed", exception);
        }
    }

    private static async Task<ProviderLimitsSnapshot> ReadSourceSafelyAsync(
        ILimitsDataSource source,
        LimitsSnapshot previous,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        bool force)
    {
        try
        {
            var result = source is IForceRefreshableLimitsDataSource forceRefreshable && force
                ? await forceRefreshable.ForceRefreshAsync(cancellationToken).ConfigureAwait(false)
                : await source.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (result.Provider != source.Provider)
            {
                throw new InvalidOperationException(
                    $"Source {source.Provider} returned data for {result.Provider}.");
            }

            WidgetLogger.Debug(
                "Limits",
                "provider_refresh_succeeded",
                ("provider", source.Provider),
                ("status", result.Status),
                ("windowCount", result.Windows.Count));
            return Normalize(result, now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            WidgetLogger.Error(
                "Limits",
                "provider_refresh_failed",
                exception,
                ("provider", source.Provider),
                ("force", force));
            if (previous.TryGetProvider(source.Provider, out var lastKnown))
            {
                return lastKnown with
                {
                    Status = LimitDataStatus.Stale,
                    ErrorMessage = exception.Message
                };
            }

            return ProviderLimitsSnapshot.Unavailable(source.Provider, now, exception.Message);
        }
    }

    private static ProviderLimitsSnapshot Normalize(
        ProviderLimitsSnapshot snapshot,
        DateTimeOffset observedAt)
    {
        var windows = snapshot.Windows
            .Select(window => window with { RemainingPercent = window.SafeRemainingPercent })
            .ToArray();
        return snapshot with
        {
            ObservedAt = snapshot.ObservedAt == default ? observedAt : snapshot.ObservedAt,
            Windows = windows
        };
    }
}
