using System.Collections.Immutable;

namespace LLMLimitsWidget.Domain;

public abstract record DomainCommand(DateTimeOffset NowUtc, Guid CorrelationId);

public sealed record StartApplicationCommand(DateTimeOffset NowUtc, Guid CorrelationId)
    : DomainCommand(NowUtc, CorrelationId);

public sealed record StopApplicationCommand(DateTimeOffset NowUtc, Guid CorrelationId)
    : DomainCommand(NowUtc, CorrelationId);

public sealed record RequestProviderRefreshCommand(
    ProviderId Provider,
    RefreshReason Reason,
    DateTimeOffset NowUtc,
    Guid CorrelationId)
    : DomainCommand(NowUtc, CorrelationId);

public sealed record RuntimeReadyCommand(
    ProviderId Provider,
    bool RefreshDue,
    DateTimeOffset NowUtc,
    Guid CorrelationId)
    : DomainCommand(NowUtc, CorrelationId);

public sealed record RuntimeStartFailedCommand(
    ProviderId Provider,
    bool RetryAllowed,
    PipelineLifecycleError Error,
    DateTimeOffset NowUtc,
    Guid CorrelationId)
    : DomainCommand(NowUtc, CorrelationId);

public sealed record RuntimeFaultedCommand(
    ProviderId Provider,
    bool RestartAllowed,
    PipelineLifecycleError Error,
    DateTimeOffset NowUtc,
    Guid CorrelationId)
    : DomainCommand(NowUtc, CorrelationId);

public sealed record WakeElapsedCommand(
    ProviderId Provider,
    WakeId Wake,
    DateTimeOffset NowUtc,
    Guid CorrelationId)
    : DomainCommand(NowUtc, CorrelationId);

public sealed record RuntimeStoppedCommand(
    ProviderId Provider,
    DateTimeOffset NowUtc,
    Guid CorrelationId)
    : DomainCommand(NowUtc, CorrelationId);

public sealed record AttemptContext(
    ProviderId Provider,
    TransportId Transport,
    AttemptId Attempt,
    EffectId Effect,
    long Generation,
    long Sequence,
    RefreshReasonSet Reasons,
    DateTimeOffset DeadlineUtc);

public abstract record AttemptOutcome;

public sealed record AttemptSucceeded(ProviderObservationEnvelope Observation)
    : AttemptOutcome;

public sealed record AttemptFailed(DomainError Error)
    : AttemptOutcome;

public sealed record AttemptCompletedCommand(
    ProviderId Provider,
    AttemptContext Context,
    AttemptOutcome Outcome,
    DateTimeOffset NowUtc,
    Guid CorrelationId)
    : DomainCommand(NowUtc, CorrelationId);

public sealed record ObservationReceivedCommand(
    ProviderId Provider,
    ProviderObservationEnvelope Observation,
    DateTimeOffset NowUtc,
    Guid CorrelationId)
    : DomainCommand(NowUtc, CorrelationId);

public sealed record TransportObservationFailedCommand(
    ProviderId Provider,
    TransportId Transport,
    DomainError Error,
    DateTimeOffset NowUtc,
    Guid CorrelationId)
    : DomainCommand(NowUtc, CorrelationId);

public sealed record RestoreProviderCacheCommand(
    ProviderId Provider,
    ProviderLimits Limits,
    DateTimeOffset NowUtc,
    Guid CorrelationId)
    : DomainCommand(NowUtc, CorrelationId);

public sealed record ProviderCacheReadFailedCommand(
    ProviderId Provider,
    PersistenceError Error,
    DateTimeOffset NowUtc,
    Guid CorrelationId)
    : DomainCommand(NowUtc, CorrelationId);

public sealed record ProviderCacheSavedCommand(
    ProviderId Provider,
    DateTimeOffset NowUtc,
    Guid CorrelationId)
    : DomainCommand(NowUtc, CorrelationId);

public sealed record ProviderCacheSaveFailedCommand(
    ProviderId Provider,
    PersistenceError Error,
    DateTimeOffset NowUtc,
    Guid CorrelationId)
    : DomainCommand(NowUtc, CorrelationId);

public abstract record DomainEffect(EffectId Id, ProviderId Provider);

public sealed record StartRuntimeEffect(EffectId Id, ProviderId Provider)
    : DomainEffect(Id, Provider);

public sealed record RestartRuntimeEffect(EffectId Id, ProviderId Provider)
    : DomainEffect(Id, Provider);

public sealed record StopRuntimeEffect(EffectId Id, ProviderId Provider, bool Force)
    : DomainEffect(Id, Provider);

public sealed record RunProviderAttemptEffect(
    EffectId Id,
    AttemptContext Context)
    : DomainEffect(Id, Context.Provider);

public sealed record ScheduleWakeEffect(
    EffectId Id,
    ProviderId Provider,
    WakeId Wake,
    DateTimeOffset DueAtUtc)
    : DomainEffect(Id, Provider);

public sealed record SaveProviderCacheEffect(
    EffectId Id,
    ProviderId Provider,
    ProviderLimits Limits)
    : DomainEffect(Id, Provider);

public abstract record DomainEvent(
    ProviderId? Provider,
    long Revision,
    Guid CorrelationId);

public sealed record StateTransitionEvent(
    ProviderId? Provider,
    long Revision,
    Guid CorrelationId,
    string Name)
    : DomainEvent(Provider, Revision, CorrelationId);

public sealed record DomainTransition(
    AppState State,
    ImmutableArray<DomainEvent> Events,
    ImmutableArray<DomainEffect> Effects)
{
    public bool Changed => State.Revision > 0;
}
