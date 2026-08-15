using System.Collections.Immutable;
using System.Threading.Channels;
using LLMLimitsWidget.Domain;

namespace LLMLimitsWidget.Application;

/// <summary>
/// Single-writer owner of AppState. Priority commands are drained before the
/// ordinary lane; refresh hints are coalesced by the caller/runtime boundary.
/// </summary>
public sealed class AppStore : IApplicationCommandSink, IAsyncDisposable
{
    private readonly Channel<DomainCommand> _priority;
    private readonly Channel<DomainCommand> _ordinary;
    private readonly IApplicationEffectExecutor _effects;
    private readonly object _sync = new();
    private AppState _current;
    private CancellationTokenSource? _lifetime;
    private Task? _loop;
    private bool _accepting = true;

    public AppStore(
        IApplicationEffectExecutor effects,
        AppState? initialState = null,
        int ordinaryCapacity = 128)
    {
        _effects = effects;
        _current = initialState ?? AppState.Empty;
        _priority = Channel.CreateBounded<DomainCommand>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _ordinary = Channel.CreateBounded<DomainCommand>(new BoundedChannelOptions(ordinaryCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    public AppState Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public event Action<AppState, DomainTransition>? StateChanged;

    public void Start(CancellationToken applicationStopping = default)
    {
        lock (_sync)
        {
            if (_loop is not null)
            {
                return;
            }

            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(applicationStopping);
            _loop = RunAsync(_lifetime.Token);
        }
    }

    public ValueTask DispatchAsync(
        DomainCommand command,
        bool priority = false,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_accepting)
            {
                throw new InvalidOperationException("The application store is closed.");
            }
        }

        return (priority ? _priority.Writer : _ordinary.Writer).WriteAsync(command, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        CancellationTokenSource? lifetime;
        lock (_sync)
        {
            _accepting = false;
            _priority.Writer.TryComplete();
            _ordinary.Writer.TryComplete();
            loop = _loop;
            lifetime = _lifetime;
            _loop = null;
            _lifetime = null;
        }

        if (lifetime is null)
        {
            return;
        }

        lifetime.Cancel();
        try
        {
            if (loop is not null)
            {
                await loop.ConfigureAwait(false);
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

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await WaitForCommandAsync(cancellationToken).ConfigureAwait(false) is { } command)
            {
                var transition = AppReducer.Reduce(Current, command);
                if (transition.State == Current)
                {
                    continue;
                }

                lock (_sync)
                {
                    _current = transition.State;
                }

                try
                {
                    StateChanged?.Invoke(transition.State, transition);
                }
                catch
                {
                    // Observer isolation belongs at this boundary. The store state is already committed.
                }

                foreach (var effect in transition.Effects)
                {
                    await _effects.ExecuteAsync(effect, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<DomainCommand?> WaitForCommandAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_priority.Reader.TryRead(out var priorityCommand))
            {
                return priorityCommand;
            }

            if (_ordinary.Reader.TryRead(out var ordinaryCommand))
            {
                return ordinaryCommand;
            }

            using var waitLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var priorityWait = _priority.Reader.WaitToReadAsync(waitLifetime.Token).AsTask();
            var ordinaryWait = _ordinary.Reader.WaitToReadAsync(waitLifetime.Token).AsTask();
            await Task.WhenAny(priorityWait, ordinaryWait).ConfigureAwait(false);
            waitLifetime.Cancel();

            if (priorityWait.IsCompletedSuccessfully && priorityWait.Result is false
                && ordinaryWait.IsCompletedSuccessfully && ordinaryWait.Result is false)
            {
                return null;
            }
        }

        return null;
    }
}

public sealed class ProviderEffectExecutor : IApplicationEffectExecutor, IAsyncDisposable
{
    private readonly ImmutableDictionary<ProviderId, IProviderRuntime> _runtimes;

    public ProviderEffectExecutor(IEnumerable<IProviderRuntime> runtimes)
    {
        _runtimes = runtimes.ToImmutableDictionary(runtime => runtime.Provider);
    }

    public ValueTask ExecuteAsync(DomainEffect effect, CancellationToken cancellationToken = default) =>
        _runtimes[effect.Provider].ExecuteAsync(effect, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        foreach (var runtime in _runtimes.Values)
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
        }
    }
}
