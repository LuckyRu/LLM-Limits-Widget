using System.Collections.Immutable;

namespace LLMLimitsWidget.Domain;

public enum ProviderId
{
    Codex,
    Claude
}

public enum TransportId
{
    CodexAppServer,
    ClaudeStatusLine,
    ClaudeDirectCli,
    ProviderCache,
    PipelineRuntime
}

public enum LimitPeriod
{
    FiveHours,
    SevenDays
}

public enum ObservationCompleteness
{
    Partial,
    Complete
}

public enum DataFreshness
{
    Missing,
    Fresh,
    Aging,
    Stale,
    Expired
}

public enum PipelinePhase
{
    Created,
    Starting,
    Idle,
    Waiting,
    Refreshing,
    BackingOff,
    RuntimeRestartBackoff,
    CircuitOpen,
    HalfOpen,
    Suspended,
    ActionRequired,
    Stopping,
    ForceStopping,
    Stopped,
    Faulted
}

public enum RuntimeLifecycle
{
    Created,
    Starting,
    Running,
    Suspended,
    Stopping,
    Stopped,
    Faulted
}

public enum AppLifecycleState
{
    Created,
    Starting,
    Running,
    Stopping,
    Stopped
}

public enum TransportHealth
{
    Unknown,
    Healthy,
    Degraded,
    ActionRequired,
    Faulted
}

public enum PersistenceHealth
{
    Unknown,
    Healthy,
    Degraded
}

public enum ProviderHealth
{
    Unknown,
    Healthy,
    Degraded,
    ActionRequired,
    Faulted
}

[Flags]
public enum RefreshReason
{
    None = 0,
    Startup = 1,
    Timer = 2,
    Manual = 4,
    PushSignal = 8,
    Resume = 16,
    Recovery = 32
}

public readonly record struct RefreshReasonSet(RefreshReason Value)
{
    public bool IsEmpty => Value == RefreshReason.None;

    public RefreshReasonSet Add(RefreshReason reason) => new(Value | reason);

    public bool Contains(RefreshReason reason) => (Value & reason) == reason;

    public static RefreshReasonSet Empty => new(RefreshReason.None);
}

public readonly record struct ObservationId(Guid Value)
{
    public static ObservationId New() => new(Guid.NewGuid());
}

public readonly record struct AttemptId(Guid Value)
{
    public static AttemptId New() => new(Guid.NewGuid());
}

public readonly record struct EffectId(Guid Value)
{
    public static EffectId New() => new(Guid.NewGuid());
}

public readonly record struct WakeId(Guid Value)
{
    public static WakeId New() => new(Guid.NewGuid());
}

public readonly record struct RemainingPercent(decimal Value)
{
    public static DomainResult<RemainingPercent> Create(
        decimal value,
        ProviderId provider,
        TransportId transport,
        DateTimeOffset nowUtc)
    {
        if (decimal.IsNegative(value) || value > 100)
        {
            return DomainResult<RemainingPercent>.Failure(new ProviderAcquisitionErrorForValidation(
                provider,
                transport,
                ErrorCode.InvalidPercentage,
                nowUtc));
        }

        return DomainResult<RemainingPercent>.Success(new RemainingPercent(value));
    }
}

public sealed record ProviderAcquisitionErrorForValidation(
    ProviderId Provider,
    TransportId Transport,
    ErrorCode Code,
    DateTimeOffset OccurredAtUtc)
    : DomainError(
        Provider,
        Transport,
        Code,
        ErrorCategory.InvalidPayload,
        RetryDisposition.WaitForVersionChange,
        UserAction.OpenDiagnostics,
        "invalid_percentage",
        OccurredAtUtc);

public sealed record ObservationCursor(
    long Generation,
    long Sequence,
    DateTimeOffset CapturedAtUtc,
    string? SourceRevision);

public sealed record DataProvenance(
    TransportId Transport,
    DateTimeOffset CapturedAtUtc,
    string? SourceRevision);

public sealed record LimitWindowCandidate(
    LimitPeriod Period,
    RemainingPercent Remaining,
    DateTimeOffset ResetAtUtc,
    ObservationCursor Cursor,
    DataProvenance Provenance);

public sealed record LimitWindow(
    LimitPeriod Period,
    RemainingPercent Remaining,
    DateTimeOffset ResetAtUtc,
    ObservationCursor Cursor,
    DataProvenance Provenance);

public sealed record ProviderLimits(
    ProviderId Provider,
    ObservationId ObservationId,
    DateTimeOffset ObservedAtUtc,
    ImmutableDictionary<LimitPeriod, LimitWindow> Windows);

public sealed record ProviderObservationEnvelope(
    ProviderId Provider,
    TransportId Transport,
    long Generation,
    long Sequence,
    string? SourceRevision,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset ReceivedAtUtc,
    ObservationCompleteness Completeness,
    ImmutableDictionary<LimitPeriod, LimitWindowCandidate> Windows,
    EffectId EffectId);
