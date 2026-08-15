using System.IO;
using System.Text.Json;

namespace LLMLimitsWidget.FloatingOverlay;

public enum ProviderFailureKind
{
    Transient,
    Authentication,
    Incompatible,
    InvalidData
}

public sealed record ProviderRefreshPolicy(
    TimeSpan HealthyInterval,
    TimeSpan MaxBackoff,
    int BreakerFailureThreshold = 3)
{
    public static ProviderRefreshPolicy For(LimitProviderId provider) => provider switch
    {
        LimitProviderId.Codex => new(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30)),
        LimitProviderId.Claude => new(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30)),
        _ => new(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30))
    };
}

/// <summary>
/// Owns exactly one provider transport. The supervisor serializes all work,
/// persists the last known good state and heals transient transport failures
/// without allowing a rapid process-launch loop.
/// </summary>
public sealed class ProviderSupervisor : IForceRefreshableLimitsDataSource, IAsyncDisposable
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(45),
        TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)
    ];

    private readonly ILimitsDataSource _transport;
    private readonly ProviderStateStore _stateStore;
    private readonly ProviderRefreshPolicy _policy;
    private readonly SemaphoreSlim _singleFlight = new(1, 1);
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly object _sync = new();
    private CancellationTokenSource? _lifetime;
    private Task? _loop;
    private ProviderLimitsSnapshot? _current;
    private DateTimeOffset _nextAttemptAt = DateTimeOffset.MinValue;
    private int _consecutiveFailures;
    private bool _breakerOpen;

    public ProviderSupervisor(
        ILimitsDataSource transport,
        ProviderStateStore? stateStore = null,
        ProviderRefreshPolicy? policy = null)
    {
        _transport = transport;
        _stateStore = stateStore ?? new ProviderStateStore(transport.Provider);
        _policy = policy ?? ProviderRefreshPolicy.For(transport.Provider);
        _current = _stateStore.TryLoad();

        if (transport is ILimitsUpdateSignalSource signalSource)
        {
            signalSource.UpdateAvailable += Transport_UpdateAvailable;
        }
    }

    public LimitProviderId Provider => _transport.Provider;

    public event EventHandler<ProviderLimitsSnapshot>? SnapshotUpdated;

    public void Start()
    {
        lock (_sync)
        {
            if (_loop is not null)
            {
                return;
            }

            _lifetime = new CancellationTokenSource();
            _loop = RunAsync(_lifetime.Token);
        }
    }

    public Task<ProviderLimitsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        RefreshInternalAsync(force: false, cancellationToken);

    public Task<ProviderLimitsSnapshot> ForceRefreshAsync(CancellationToken cancellationToken) =>
        RefreshInternalAsync(force: true, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? lifetime;
        Task? loop;
        lock (_sync)
        {
            lifetime = _lifetime;
            loop = _loop;
            _lifetime = null;
            _loop = null;
        }

        if (lifetime is not null)
        {
            lifetime.Cancel();
            try
            {
                if (loop is not null)
                {
                    await loop.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                lifetime.Dispose();
            }
        }

        if (_transport is ILimitsUpdateSignalSource signalSource)
        {
            signalSource.UpdateAvailable -= Transport_UpdateAvailable;
        }

        if (_transport is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }

        _singleFlight.Dispose();
        _wakeSignal.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await RefreshInternalAsync(force: false, cancellationToken).ConfigureAwait(false);
            var delay = GetDelayUntilNextAttempt();
            try
            {
                var wakeTask = _wakeSignal.WaitAsync(cancellationToken);
                var delayTask = Task.Delay(delay, cancellationToken);
                await Task.WhenAny(wakeTask, delayTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<ProviderLimitsSnapshot> RefreshInternalAsync(bool force, CancellationToken cancellationToken)
    {
        await _singleFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var current = ReadCurrent();
            if (!force && now < _nextAttemptAt && current is not null)
            {
                return WithFreshness(current, now);
            }

            try
            {
                var result = _transport is IForceRefreshableLimitsDataSource forceable && force
                    ? await forceable.ForceRefreshAsync(cancellationToken).ConfigureAwait(false)
                    : await _transport.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
                if (result.Provider != Provider)
                {
                    throw new InvalidDataException($"{Provider} transport returned {result.Provider}.");
                }

                if (!SnapshotValidation.TryNormalize(result, out var normalized, out var reason))
                {
                    throw new InvalidDataException($"{Provider} returned invalid limits: {reason}.");
                }

                await _stateStore.SaveAsync(normalized, cancellationToken).ConfigureAwait(false);
                lock (_sync)
                {
                    _current = normalized;
                    _consecutiveFailures = 0;
                    _breakerOpen = false;
                    _nextAttemptAt = now + _policy.HealthyInterval;
                }
                Publish(normalized);
                WidgetLogger.Info("ProviderSupervisor", "refresh_succeeded",
                    ("provider", Provider), ("nextRefreshSeconds", _policy.HealthyInterval.TotalSeconds));
                return normalized;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var kind = ClassifyFailure(exception);
                ProviderLimitsSnapshot? fallback;
                TimeSpan delay;
                lock (_sync)
                {
                    _consecutiveFailures++;
                    _breakerOpen = _consecutiveFailures >= _policy.BreakerFailureThreshold;
                    delay = GetRetryDelay(_consecutiveFailures, kind);
                    _nextAttemptAt = now + delay;
                    fallback = _current;
                }

                WidgetLogger.Warning("ProviderSupervisor", "refresh_failed", exception,
                    ("provider", Provider),
                    ("failureKind", kind),
                    ("consecutiveFailures", _consecutiveFailures),
                    ("breakerOpen", _breakerOpen),
                    ("retrySeconds", delay.TotalSeconds));
                var status = kind is ProviderFailureKind.Authentication or ProviderFailureKind.Incompatible
                    ? LimitDataStatus.ActionRequired
                    : LimitDataStatus.Stale;
                var published = fallback is not null
                    ? fallback with { Status = status, ErrorMessage = exception.Message }
                    : ProviderLimitsSnapshot.Unavailable(Provider, now, exception.Message) with { Status = status };
                lock (_sync)
                {
                    _current = published;
                }
                Publish(published);
                return published;
            }
        }
        finally
        {
            _singleFlight.Release();
        }
    }

    private ProviderLimitsSnapshot? ReadCurrent()
    {
        lock (_sync)
        {
            return _current;
        }
    }

    private ProviderLimitsSnapshot WithFreshness(ProviderLimitsSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.Windows.Count == 0 || snapshot.Status != LimitDataStatus.Fresh)
        {
            return snapshot;
        }

        var age = now - snapshot.ObservedAt.ToUniversalTime();
        return age <= _policy.HealthyInterval.Add(_policy.HealthyInterval)
            ? snapshot with { Status = LimitDataStatus.Fresh, ErrorMessage = null }
            : snapshot with { Status = LimitDataStatus.Aging };
    }

    private TimeSpan GetDelayUntilNextAttempt()
    {
        DateTimeOffset next;
        lock (_sync)
        {
            next = _nextAttemptAt;
        }

        var delay = next - DateTimeOffset.UtcNow;
        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }

    private TimeSpan GetRetryDelay(int failures, ProviderFailureKind kind)
    {
        if (kind is ProviderFailureKind.Authentication or ProviderFailureKind.Incompatible)
        {
            return TimeSpan.FromMinutes(30);
        }

        var baseDelay = RetryDelays[Math.Min(failures - 1, RetryDelays.Length - 1)];
        var jitteredMilliseconds = Random.Shared.NextDouble() * baseDelay.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(Math.Min(jitteredMilliseconds, _policy.MaxBackoff.TotalMilliseconds));
    }

    private static ProviderFailureKind ClassifyFailure(Exception exception)
    {
        if (exception is TimeoutException or IOException)
        {
            return ProviderFailureKind.Transient;
        }

        if (exception is InvalidDataException or JsonException)
        {
            return ProviderFailureKind.InvalidData;
        }

        var message = exception.Message;
        if (message.Contains("login", StringComparison.OrdinalIgnoreCase)
            || message.Contains("auth", StringComparison.OrdinalIgnoreCase)
            || message.Contains("sign in", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderFailureKind.Authentication;
        }

        if (message.Contains("protocol", StringComparison.OrdinalIgnoreCase)
            || message.Contains("rate-limit bucket", StringComparison.OrdinalIgnoreCase)
            || message.Contains("supported limit windows", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderFailureKind.Incompatible;
        }

        return ProviderFailureKind.Transient;
    }

    private void Transport_UpdateAvailable(object? sender, EventArgs e)
    {
        try
        {
            _wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // One pending wake-up is sufficient.
        }
    }

    private void Publish(ProviderLimitsSnapshot snapshot)
    {
        try
        {
            SnapshotUpdated?.Invoke(this, snapshot);
        }
        catch (Exception exception)
        {
            WidgetLogger.Error("ProviderSupervisor", "snapshot_subscriber_failed", exception,
                ("provider", Provider));
        }
    }
}
