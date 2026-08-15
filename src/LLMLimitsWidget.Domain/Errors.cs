namespace LLMLimitsWidget.Domain;

public enum ErrorCategory
{
    Transient,
    Authentication,
    Compatibility,
    InvalidPayload,
    Configuration,
    Persistence,
    Lifecycle
}

public enum RetryDisposition
{
    Immediate,
    Backoff,
    WaitForSignal,
    WaitForUserAction,
    WaitForVersionChange,
    Never
}

public enum UserAction
{
    None,
    SignIn,
    UpdateCli,
    RepairConfiguration,
    OpenDiagnostics
}

public enum ErrorCode
{
    ExecutableNotFound,
    ProcessStartFailed,
    ProcessExited,
    BrokenPipe,
    RequestTimeout,
    IoUnavailable,
    LoginRequired,
    SessionExpired,
    PermissionDenied,
    UnsupportedCliVersion,
    CapabilityMissing,
    ProtocolChanged,
    MalformedPayload,
    SchemaMismatch,
    NoSupportedWindows,
    InvalidPercentage,
    InvalidResetTime,
    ProviderMismatch,
    OutOfOrderObservation,
    StatusLineNotConfigured,
    SignalUnavailable,
    InvalidSnapshotPath,
    CacheReadFailed,
    CacheWriteFailed,
    CacheCorrupted,
    UnsupportedCacheSchema,
    PipelineNotStarted,
    PipelineAlreadyStopped,
    CommandQueueClosed,
    UnexpectedPipelineTermination,
    ShutdownTimeout
}

public abstract record DomainError(
    ProviderId Provider,
    TransportId Transport,
    ErrorCode Code,
    ErrorCategory Category,
    RetryDisposition Retry,
    UserAction UserAction,
    string DiagnosticId,
    DateTimeOffset OccurredAtUtc);

public abstract record ProviderAcquisitionError(
    ProviderId Provider,
    TransportId Transport,
    ErrorCode Code,
    ErrorCategory Category,
    RetryDisposition Retry,
    UserAction UserAction,
    string DiagnosticId,
    DateTimeOffset OccurredAtUtc)
    : DomainError(Provider, Transport, Code, Category, Retry, UserAction, DiagnosticId, OccurredAtUtc);

public sealed record CodexAcquisitionError(
    ErrorCode Code,
    ErrorCategory Category,
    RetryDisposition Retry,
    UserAction UserAction,
    string DiagnosticId,
    DateTimeOffset OccurredAtUtc)
    : ProviderAcquisitionError(
        ProviderId.Codex,
        TransportId.CodexAppServer,
        Code,
        Category,
        Retry,
        UserAction,
        DiagnosticId,
        OccurredAtUtc);

public sealed record ClaudeStatusLineError(
    ErrorCode Code,
    ErrorCategory Category,
    RetryDisposition Retry,
    UserAction UserAction,
    string DiagnosticId,
    DateTimeOffset OccurredAtUtc)
    : ProviderAcquisitionError(
        ProviderId.Claude,
        TransportId.ClaudeStatusLine,
        Code,
        Category,
        Retry,
        UserAction,
        DiagnosticId,
        OccurredAtUtc);

public sealed record ClaudeDirectError(
    ErrorCode Code,
    ErrorCategory Category,
    RetryDisposition Retry,
    UserAction UserAction,
    string DiagnosticId,
    DateTimeOffset OccurredAtUtc)
    : ProviderAcquisitionError(
        ProviderId.Claude,
        TransportId.ClaudeDirectCli,
        Code,
        Category,
        Retry,
        UserAction,
        DiagnosticId,
        OccurredAtUtc);

public sealed record PersistenceError(
    ProviderId Provider,
    ErrorCode Code,
    string DiagnosticId,
    DateTimeOffset OccurredAtUtc)
    : DomainError(
        Provider,
        TransportId.ProviderCache,
        Code,
        ErrorCategory.Persistence,
        RetryDisposition.Backoff,
        UserAction.OpenDiagnostics,
        DiagnosticId,
        OccurredAtUtc);

public sealed record PipelineLifecycleError(
    ProviderId Provider,
    ErrorCode Code,
    string DiagnosticId,
    DateTimeOffset OccurredAtUtc)
    : DomainError(
        Provider,
        TransportId.PipelineRuntime,
        Code,
        ErrorCategory.Lifecycle,
        RetryDisposition.Backoff,
        UserAction.OpenDiagnostics,
        DiagnosticId,
        OccurredAtUtc);

public sealed record DomainResult<T>(T? Value, DomainError? Error)
{
    public bool IsSuccess => Error is null;

    public static DomainResult<T> Success(T value) => new(value, null);

    public static DomainResult<T> Failure(DomainError error) => new(default, error);
}
