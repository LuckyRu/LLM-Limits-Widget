using System.Collections.Immutable;

namespace LLMLimitsWidget.Domain;

public sealed record ProviderPipelineState(
    PipelinePhase Phase,
    long Generation,
    long NextSequence,
    AttemptId? ActiveAttempt,
    RefreshReasonSet PendingReasons,
    DateTimeOffset? NextWakeAtUtc,
    WakeId? ScheduledWake,
    ImmutableArray<DateTimeOffset> RuntimeRestartHistory,
    PipelineLifecycleError? LastLifecycleError)
{
    public static ProviderPipelineState Initial => new(
        PipelinePhase.Created,
        0,
        0,
        null,
        RefreshReasonSet.Empty,
        null,
        null,
        ImmutableArray<DateTimeOffset>.Empty,
        null);
}

public sealed record TransportState(
    TransportId Transport,
    TransportHealth Health,
    DomainError? LastError,
    int ConsecutiveFailures,
    DateTimeOffset? LastAttemptAtUtc,
    DateTimeOffset? LastSuccessAtUtc)
{
    public static TransportState Initial(TransportId transport) => new(
        transport,
        TransportHealth.Unknown,
        null,
        0,
        null,
        null);
}

public sealed record PersistenceState(
    PersistenceHealth Health,
    PersistenceError? LastError,
    DateTimeOffset? LastReadAtUtc,
    DateTimeOffset? LastWriteAtUtc)
{
    public static PersistenceState Initial => new(
        PersistenceHealth.Unknown,
        null,
        null,
        null);
}

public sealed record ProviderState(
    ProviderId Provider,
    ProviderLimits? LastKnownGood,
    DataFreshness Freshness,
    ProviderPipelineState Pipeline,
    ImmutableDictionary<TransportId, TransportState> Transports,
    PersistenceState Persistence,
    ProviderHealth AggregateHealth,
    DateTimeOffset? LastAttemptAtUtc,
    DateTimeOffset? LastSuccessAtUtc,
    DateTimeOffset? NextAttemptAtUtc,
    long AcceptedGeneration,
    long AcceptedSequence)
{
    public static ProviderState Initial(ProviderId provider) => new(
        provider,
        null,
        DataFreshness.Missing,
        ProviderPipelineState.Initial,
        InitialTransports(provider),
        PersistenceState.Initial,
        ProviderHealth.Unknown,
        null,
        null,
        null,
        0,
        0);

    private static ImmutableDictionary<TransportId, TransportState> InitialTransports(
        ProviderId provider)
    {
        var builder = ImmutableDictionary.CreateBuilder<TransportId, TransportState>();
        switch (provider)
        {
            case ProviderId.Codex:
                builder[TransportId.CodexAppServer] = TransportState.Initial(TransportId.CodexAppServer);
                break;
            case ProviderId.Claude:
                builder[TransportId.ClaudeStatusLine] = TransportState.Initial(TransportId.ClaudeStatusLine);
                builder[TransportId.ClaudeDirectCli] = TransportState.Initial(TransportId.ClaudeDirectCli);
                break;
        }

        return builder.ToImmutable();
    }
}

public sealed record AppState(
    long Revision,
    AppLifecycleState Lifecycle,
    ImmutableDictionary<ProviderId, ProviderState> Providers)
{
    public static AppState Empty => new(
        0,
        AppLifecycleState.Created,
        Enum.GetValues<ProviderId>()
            .ToImmutableDictionary(provider => provider, ProviderState.Initial));
}
