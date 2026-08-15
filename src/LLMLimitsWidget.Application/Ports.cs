using LLMLimitsWidget.Domain;

namespace LLMLimitsWidget.Application;

public interface IApplicationCommandSink
{
    ValueTask DispatchAsync(DomainCommand command, bool priority = false, CancellationToken cancellationToken = default);
}

public interface IProviderAttemptTransport
{
    ProviderId Provider { get; }
    Task<AttemptOutcome> AcquireAsync(AttemptContext context, CancellationToken cancellationToken);
}

public interface IProviderRuntime : IAsyncDisposable
{
    ProviderId Provider { get; }
    RuntimeLifecycle Lifecycle { get; }
    Task StartAsync(CancellationToken applicationStopping);
    ValueTask ExecuteAsync(DomainEffect effect, CancellationToken cancellationToken = default);
    Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

public interface IApplicationEffectExecutor
{
    ValueTask ExecuteAsync(DomainEffect effect, CancellationToken cancellationToken = default);
}
