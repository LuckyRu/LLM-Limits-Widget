using System.Collections.Concurrent;
using System.Collections.Immutable;
using LLMLimitsWidget.Application;
using LLMLimitsWidget.Domain;

var failures = new List<string>();
var now = DateTimeOffset.UtcNow;

var effectRecorder = new RecordingEffectExecutor();
await using (var store = new AppStore(effectRecorder))
{
    var started = new TaskCompletionSource<AppState>(TaskCreationOptions.RunContinuationsAsynchronously);
    store.StateChanged += (state, _) =>
    {
        if (state.Lifecycle == AppLifecycleState.Starting)
        {
            started.TrySetResult(state);
        }
    };
    store.Start();
    await store.DispatchAsync(new StartApplicationCommand(now, Guid.NewGuid()));
    var starting = await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    AssertEqual(AppLifecycleState.Starting, starting.Lifecycle, "A-001 store commits one immutable state");
    AssertEqual(2, effectRecorder.Effects.OfType<StartRuntimeEffect>().Count(), "A-001 emits both runtime starts");

    var duplicateRevision = store.Current.Revision;
    await store.DispatchAsync(new StartApplicationCommand(now, Guid.NewGuid()));
    await Task.Delay(50);
    AssertEqual(duplicateRevision, store.Current.Revision, "A-002 duplicate command does not advance revision");

    var stopping = new TaskCompletionSource<AppState>(TaskCreationOptions.RunContinuationsAsynchronously);
    store.StateChanged += (state, _) =>
    {
        if (state.Lifecycle == AppLifecycleState.Stopping)
        {
            stopping.TrySetResult(state);
        }
    };
    await store.DispatchAsync(new StopApplicationCommand(now, Guid.NewGuid()), priority: true);
    await stopping.Task.WaitAsync(TimeSpan.FromSeconds(2));
    AssertEqual(2, effectRecorder.Effects.OfType<StopRuntimeEffect>().Count(), "A-003 priority stop reaches both runtimes");
}

var sink = new RecordingCommandSink();
var transport = new BlockingTransport(ProviderId.Codex);
await using (var runtime = new ProviderPipelineRuntime(transport, sink))
{
    await runtime.StartAsync(CancellationToken.None);
    await runtime.ExecuteAsync(new StartRuntimeEffect(EffectId.New(), ProviderId.Codex));
    await sink.WaitFor<RuntimeReadyCommand>();

    var context = new AttemptContext(
        ProviderId.Codex,
        TransportId.CodexAppServer,
        AttemptId.New(),
        EffectId.New(),
        0,
        1,
        new RefreshReasonSet(RefreshReason.Manual),
        DateTimeOffset.UtcNow.AddMinutes(1));
    await runtime.ExecuteAsync(new RunProviderAttemptEffect(context.Effect, context));
    await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

    await runtime.ExecuteAsync(new StopRuntimeEffect(EffectId.New(), ProviderId.Codex, Force: false));
    await transport.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await sink.WaitFor<RuntimeStoppedCommand>();
    AssertEqual(RuntimeLifecycle.Stopped, runtime.Lifecycle, "A-032 priority stop cancels tracked attempt");
}
Assert(transport.Disposed.Task.IsCompleted, "A-033 runtime disposes an owned disposable transport");

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

Console.WriteLine("Application M2: all cases passed.");
return 0;

void AssertEqual<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        failures.Add($"{name}: expected {expected}, got {actual}");
    }
}

void Assert(bool condition, string name)
{
    if (!condition)
    {
        failures.Add($"{name}: condition was false");
    }
}

sealed class RecordingEffectExecutor : IApplicationEffectExecutor
{
    public ConcurrentQueue<DomainEffect> Effects { get; } = new();

    public ValueTask ExecuteAsync(DomainEffect effect, CancellationToken cancellationToken = default)
    {
        Effects.Enqueue(effect);
        return ValueTask.CompletedTask;
    }
}

sealed class RecordingCommandSink : IApplicationCommandSink
{
    private readonly ConcurrentQueue<DomainCommand> _commands = new();
    private readonly SemaphoreSlim _signal = new(0);

    public ValueTask DispatchAsync(
        DomainCommand command,
        bool priority = false,
        CancellationToken cancellationToken = default)
    {
        _commands.Enqueue(command);
        _signal.Release();
        return ValueTask.CompletedTask;
    }

    public async Task<T> WaitFor<T>() where T : DomainCommand
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!timeout.IsCancellationRequested)
        {
            await _signal.WaitAsync(timeout.Token);
            if (_commands.TryDequeue(out var command) && command is T typed)
            {
                return typed;
            }
        }

        throw new TimeoutException($"Did not receive {typeof(T).Name}.");
    }
}

sealed class BlockingTransport(ProviderId provider) : IProviderAttemptTransport, IAsyncDisposable
{
    public ProviderId Provider => provider;
    public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<AttemptOutcome> AcquireAsync(AttemptContext context, CancellationToken cancellationToken)
    {
        Started.TrySetResult(true);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new AttemptFailed(new PipelineLifecycleError(
                Provider,
                ErrorCode.ProcessExited,
                "unexpected_completion",
                DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Cancelled.TrySetResult(true);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed.TrySetResult(true);
        return ValueTask.CompletedTask;
    }
}
