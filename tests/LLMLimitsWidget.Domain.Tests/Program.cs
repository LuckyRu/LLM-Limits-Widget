using System.Collections.Immutable;
using LLMLimitsWidget.Domain;

var failures = new List<string>();
var now = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

var validPercent = RemainingPercent.Create(
    62.5m,
    ProviderId.Codex,
    TransportId.CodexAppServer,
    now);
Assert(validPercent.IsSuccess, "D-001 accepts a valid percentage");
AssertEqual(62.5m, validPercent.Value!.Value, "D-001 preserves decimal precision");

var invalidPercent = RemainingPercent.Create(
    101m,
    ProviderId.Codex,
    TransportId.CodexAppServer,
    now);
Assert(!invalidPercent.IsSuccess, "D-002 rejects percentage above 100");
AssertEqual(ErrorCode.InvalidPercentage, invalidPercent.Error!.Code, "D-002 returns typed error");

var state = AppState.Empty;
var start = AppReducer.Reduce(state, new StartApplicationCommand(now, Guid.NewGuid()));
AssertEqual(AppLifecycleState.Starting, start.State.Lifecycle, "A-023 enters Starting");
AssertEqual(2, start.Effects.OfType<StartRuntimeEffect>().Count(), "A-023 starts both runtimes");
AssertEqual(1L, start.State.Revision, "D-014 increments revision once");

var duplicateStart = AppReducer.Reduce(start.State, new StartApplicationCommand(now, Guid.NewGuid()));
AssertEqual(start.State.Revision, duplicateStart.State.Revision, "D-019 duplicate start is a no-op");

var ready = AppReducer.Reduce(
    start.State,
    new RuntimeReadyCommand(ProviderId.Codex, RefreshDue: true, now, Guid.NewGuid()));
var codexAfterReady = ready.State.Providers[ProviderId.Codex];
AssertEqual(PipelinePhase.Refreshing, codexAfterReady.Pipeline.Phase, "A-024 ready runtime starts due refresh");
var attemptEffect = ready.Effects.OfType<RunProviderAttemptEffect>().Single();
AssertEqual(1L, attemptEffect.Context.Sequence, "A-024 assigns first sequence");

var queued = AppReducer.Reduce(
    ready.State,
    new RequestProviderRefreshCommand(ProviderId.Codex, RefreshReason.Manual, now, Guid.NewGuid()));
AssertEqual(
    RefreshReason.Manual,
    queued.State.Providers[ProviderId.Codex].Pipeline.PendingReasons.Value,
    "A-006 coalesces refresh while attempt is active");
AssertEqual(0, queued.Effects.OfType<RunProviderAttemptEffect>().Count(), "A-006 does not duplicate attempt");

var startFailed = AppReducer.Reduce(
    start.State,
    new RuntimeStartFailedCommand(
        ProviderId.Claude,
        RetryAllowed: true,
        new PipelineLifecycleError(
            ProviderId.Claude,
            ErrorCode.ProcessStartFailed,
            "runtime_start_failed",
            now),
        now,
        Guid.NewGuid()));
AssertEqual(
    PipelinePhase.RuntimeRestartBackoff,
    startFailed.State.Providers[ProviderId.Claude].Pipeline.Phase,
    "A-038 separates runtime restart backoff from provider attempt backoff");
AssertEqual(1, startFailed.Effects.OfType<ScheduleWakeEffect>().Count(), "A-038 schedules restart wake");

var restarted = AppReducer.Reduce(
    startFailed.State,
    new WakeElapsedCommand(
        ProviderId.Claude,
        startFailed.Effects.OfType<ScheduleWakeEffect>().Single().Wake,
        now.AddSeconds(1),
        Guid.NewGuid()));
AssertEqual(PipelinePhase.Starting, restarted.State.Providers[ProviderId.Claude].Pipeline.Phase, "A-044 restarts runtime after wake");
AssertEqual(1, restarted.Effects.OfType<RestartRuntimeEffect>().Count(), "A-044 emits restart effect");

var candidate = new LimitWindowCandidate(
    LimitPeriod.SevenDays,
    validPercent.Value!,
    now.AddDays(3),
    new ObservationCursor(0, attemptEffect.Context.Sequence, now, "codex-1"),
    new DataProvenance(TransportId.CodexAppServer, now, "codex-1"));
var observation = new ProviderObservationEnvelope(
    ProviderId.Codex,
    TransportId.CodexAppServer,
    attemptEffect.Context.Generation,
    attemptEffect.Context.Sequence,
    "codex-1",
    now,
    now.AddMilliseconds(20),
    ObservationCompleteness.Complete,
    ImmutableDictionary<LimitPeriod, LimitWindowCandidate>.Empty.Add(LimitPeriod.SevenDays, candidate),
    attemptEffect.Id);
var completed = AppReducer.Reduce(
    ready.State,
    new AttemptCompletedCommand(
        ProviderId.Codex,
        attemptEffect.Context,
        new AttemptSucceeded(observation),
        now.AddMilliseconds(20),
        Guid.NewGuid()));
var codexAfterSuccess = completed.State.Providers[ProviderId.Codex];
AssertEqual(PipelinePhase.Waiting, codexAfterSuccess.Pipeline.Phase, "D-004 success returns to Waiting");
AssertEqual(62.5m, codexAfterSuccess.LastKnownGood!.Windows[LimitPeriod.SevenDays].Remaining.Value, "D-004 stores LKG");
AssertEqual(1, completed.Effects.OfType<SaveProviderCacheEffect>().Count(), "D-004 schedules cache save");

var late = AppReducer.Reduce(
    completed.State,
    new AttemptCompletedCommand(
        ProviderId.Codex,
        attemptEffect.Context,
        new AttemptSucceeded(observation),
        now.AddMinutes(1),
        Guid.NewGuid()));
AssertEqual(completed.State.Revision, late.State.Revision, "D-006 late completion is idempotent");
AssertEqual(completed.State, late.State, "D-006 late completion cannot roll back LKG");

var stop = AppReducer.Reduce(completed.State, new StopApplicationCommand(now, Guid.NewGuid()));
AssertEqual(AppLifecycleState.Stopping, stop.State.Lifecycle, "A-013 enters Stopping");
AssertEqual(2, stop.Effects.OfType<StopRuntimeEffect>().Count(), "A-013 stops both runtimes");

var stopCodex = AppReducer.Reduce(stop.State, new RuntimeStoppedCommand(ProviderId.Codex, now, Guid.NewGuid()));
var stopClaude = AppReducer.Reduce(stopCodex.State, new RuntimeStoppedCommand(ProviderId.Claude, now, Guid.NewGuid()));
AssertEqual(AppLifecycleState.Stopped, stopClaude.State.Lifecycle, "A-034 closes app after final runtime transition");

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

Console.WriteLine("Domain M1: all cases passed.");
return 0;

void Assert(bool condition, string name)
{
    if (!condition)
    {
        failures.Add($"{name}: condition was false");
    }
}

void AssertEqual<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        failures.Add($"{name}: expected {expected}, got {actual}");
    }
}
