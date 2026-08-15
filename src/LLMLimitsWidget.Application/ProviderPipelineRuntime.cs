using System.Threading.Channels;
using LLMLimitsWidget.Domain;

namespace LLMLimitsWidget.Application;

/// <summary>
/// Executes domain effects for exactly one provider. The actor loop never
/// awaits provider IO; attempts are tracked tasks and report completion back
/// to AppStore through the priority command lane.
/// </summary>
public sealed class ProviderPipelineRuntime : IProviderRuntime
{
    private readonly IProviderAttemptTransport _transport;
    private readonly IApplicationCommandSink _commands;
    private readonly TimeProvider _clock;
    private readonly Channel<RuntimeWork> _priority;
    private readonly Channel<RuntimeWork> _ordinary;
    private readonly object _sync = new();
    private CancellationTokenSource? _lifetime;
    private Task? _loop;
    private Task? _attemptCompletion;
    private CancellationTokenSource? _attemptLifetime;
    private Task? _wakeTask;
    private RuntimeLifecycle _lifecycle = RuntimeLifecycle.Created;

    public ProviderPipelineRuntime(
        IProviderAttemptTransport transport,
        IApplicationCommandSink commands,
        TimeProvider? clock = null,
        int ordinaryCapacity = 32)
    {
        _transport = transport;
        _commands = commands;
        _clock = clock ?? TimeProvider.System;
        _priority = Channel.CreateBounded<RuntimeWork>(new BoundedChannelOptions(8)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _ordinary = Channel.CreateBounded<RuntimeWork>(new BoundedChannelOptions(ordinaryCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    public ProviderId Provider => _transport.Provider;

    public RuntimeLifecycle Lifecycle
    {
        get
        {
            lock (_sync)
            {
                return _lifecycle;
            }
        }
    }

    public Task StartAsync(CancellationToken applicationStopping)
    {
        lock (_sync)
        {
            if (_loop is not null)
            {
                return Task.CompletedTask;
            }

            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(applicationStopping);
            _lifecycle = RuntimeLifecycle.Starting;
            _loop = RunAsync(_lifetime.Token);
            return Task.CompletedTask;
        }
    }

    public ValueTask ExecuteAsync(DomainEffect effect, CancellationToken cancellationToken = default)
    {
        var work = new RuntimeWork(effect, cancellationToken);
        var priority = effect is StopRuntimeEffect { Force: true } or StopRuntimeEffect
            or RestartRuntimeEffect or StartRuntimeEffect;
        return (priority ? _priority.Writer : _ordinary.Writer).WriteAsync(work, cancellationToken);
    }

    public async Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Task? loop;
        lock (_sync)
        {
            loop = _loop;
            if (loop is null)
            {
                _lifecycle = RuntimeLifecycle.Stopped;
                return;
            }
        }

        await ExecuteAsync(new StopRuntimeEffect(EffectId.New(), Provider, Force: false), cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await loop.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await ExecuteAsync(new StopRuntimeEffect(EffectId.New(), Provider, Force: true), cancellationToken)
                .ConfigureAwait(false);
            await loop.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? lifetime;
        lock (_sync)
        {
            lifetime = _lifetime;
        }

        if (lifetime is not null)
        {
            lifetime.Cancel();
        }

        Task? loop;
        lock (_sync)
        {
            loop = _loop;
        }

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        lifetime?.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await WaitForWorkAsync(cancellationToken).ConfigureAwait(false) is { } work)
            {
                await ExecuteWorkAsync(work, cancellationToken).ConfigureAwait(false);
                if (Lifecycle == RuntimeLifecycle.Stopped)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetLifecycle(RuntimeLifecycle.Stopped);
        }
        catch
        {
            SetLifecycle(RuntimeLifecycle.Faulted);
            throw;
        }
    }

    private async Task ExecuteWorkAsync(RuntimeWork work, CancellationToken cancellationToken)
    {
        switch (work.Effect)
        {
            case StartRuntimeEffect:
            case RestartRuntimeEffect:
                SetLifecycle(RuntimeLifecycle.Running);
                await _commands.DispatchAsync(
                    new RuntimeReadyCommand(Provider, true, _clock.GetUtcNow(), Guid.NewGuid()),
                    priority: true,
                    cancellationToken).ConfigureAwait(false);
                break;
            case RunProviderAttemptEffect attempt:
                StartAttempt(attempt.Context, cancellationToken);
                break;
            case ScheduleWakeEffect wake:
                ScheduleWake(wake, cancellationToken);
                break;
            case StopRuntimeEffect stop:
                await StopInternalAsync(stop.Force, cancellationToken).ConfigureAwait(false);
                break;
            default:
                break;
        }
    }

    private void StartAttempt(AttemptContext context, CancellationToken runtimeCancellation)
    {
        lock (_sync)
        {
            if (_attemptCompletion is not null && !_attemptCompletion.IsCompleted)
            {
                return;
            }

            _attemptLifetime?.Dispose();
            _attemptLifetime = CancellationTokenSource.CreateLinkedTokenSource(runtimeCancellation);
            _attemptCompletion = CompleteAttemptAsync(context, _attemptLifetime.Token);
        }
    }

    private async Task CompleteAttemptAsync(AttemptContext context, CancellationToken cancellationToken)
    {
        AttemptOutcome outcome;
        try
        {
            outcome = await _transport.AcquireAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            outcome = new AttemptFailed(new PipelineLifecycleError(
                Provider,
                ErrorCode.ProcessExited,
                "transport_exception",
                _clock.GetUtcNow()));
        }

        await _commands.DispatchAsync(
            new AttemptCompletedCommand(
                Provider,
                context,
                outcome,
                _clock.GetUtcNow(),
                Guid.NewGuid()),
            priority: true,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
    }

    private void ScheduleWake(ScheduleWakeEffect effect, CancellationToken runtimeCancellation)
    {
        _wakeTask = WaitAndDispatchWakeAsync(effect, runtimeCancellation);
    }

    private async Task WaitAndDispatchWakeAsync(ScheduleWakeEffect effect, CancellationToken cancellationToken)
    {
        try
        {
            var delay = effect.DueAtUtc - _clock.GetUtcNow();
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _clock, cancellationToken).ConfigureAwait(false);
            }
            await _commands.DispatchAsync(
                new WakeElapsedCommand(effect.Provider, effect.Wake, _clock.GetUtcNow(), Guid.NewGuid()),
                priority: true,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task StopInternalAsync(bool force, CancellationToken cancellationToken)
    {
        SetLifecycle(RuntimeLifecycle.Stopping);
        _attemptLifetime?.Cancel();
        if (_wakeTask is not null && !force)
        {
            try
            {
                await _wakeTask.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }

        SetLifecycle(RuntimeLifecycle.Stopped);
        await _commands.DispatchAsync(
            new RuntimeStoppedCommand(Provider, _clock.GetUtcNow(), Guid.NewGuid()),
            priority: true,
            cancellationToken).ConfigureAwait(false);
        _lifetime?.Cancel();
    }

    private void SetLifecycle(RuntimeLifecycle lifecycle)
    {
        lock (_sync)
        {
            _lifecycle = lifecycle;
        }
    }

    private async Task<RuntimeWork?> WaitForWorkAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_priority.Reader.TryRead(out var priorityWork))
            {
                return priorityWork;
            }

            if (_ordinary.Reader.TryRead(out var ordinaryWork))
            {
                return ordinaryWork;
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

    private sealed record RuntimeWork(DomainEffect Effect, CancellationToken CancellationToken);
}
