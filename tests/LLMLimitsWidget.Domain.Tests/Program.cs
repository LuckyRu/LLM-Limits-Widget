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

var claudeReady = AppReducer.Reduce(
    ready.State,
    new RuntimeReadyCommand(ProviderId.Claude, RefreshDue: true, now, Guid.NewGuid()));
AssertEqual(AppLifecycleState.Running, claudeReady.State.Lifecycle, "A-025 enters Running after both runtimes are ready");

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
var healthyWake = completed.Effects.OfType<ScheduleWakeEffect>().Single();
AssertEqual(now.AddMilliseconds(20).AddMinutes(2), healthyWake.DueAtUtc, "D-005 schedules next Codex refresh after success");
var staleWake = AppReducer.Reduce(
    completed.State,
    new WakeElapsedCommand(ProviderId.Codex, WakeId.New(), now.AddMinutes(2), Guid.NewGuid()));
AssertEqual(completed.State.Revision, staleWake.State.Revision, "D-005 ignores stale wake identity");
var periodicRetry = AppReducer.Reduce(
    completed.State,
    new WakeElapsedCommand(ProviderId.Codex, healthyWake.Wake, healthyWake.DueAtUtc, Guid.NewGuid()));
AssertEqual(PipelinePhase.Refreshing, periodicRetry.State.Providers[ProviderId.Codex].Pipeline.Phase, "D-005 starts scheduled refresh");

var queuedCompletion = AppReducer.Reduce(
    queued.State,
    new AttemptCompletedCommand(
        ProviderId.Codex,
        attemptEffect.Context,
        new AttemptSucceeded(observation),
        now.AddMilliseconds(20),
        Guid.NewGuid()));
AssertEqual(PipelinePhase.Refreshing, queuedCompletion.State.Providers[ProviderId.Codex].Pipeline.Phase, "A-006 runs queued refresh after active attempt");
AssertEqual(1, queuedCompletion.Effects.OfType<RunProviderAttemptEffect>().Count(), "A-006 emits queued attempt effect");

var compatibilityFailure = AppReducer.Reduce(
    ready.State,
    new AttemptCompletedCommand(
        ProviderId.Codex,
        attemptEffect.Context,
        new AttemptFailed(new CodexAcquisitionError(
            ErrorCode.CapabilityMissing,
            ErrorCategory.Compatibility,
            RetryDisposition.WaitForVersionChange,
            UserAction.UpdateCli,
            "capability_missing",
            now)),
        now,
        Guid.NewGuid()));
AssertEqual(PipelinePhase.ActionRequired, compatibilityFailure.State.Providers[ProviderId.Codex].Pipeline.Phase, "D-007 compatibility error stops automatic retry");
AssertEqual(0, compatibilityFailure.Effects.OfType<ScheduleWakeEffect>().Count(), "D-007 version change does not poll");

var claudeStatusRemaining = RemainingPercent.Create(
    70m,
    ProviderId.Claude,
    TransportId.ClaudeStatusLine,
    now).Value!;
var claudeDirectRemaining = RemainingPercent.Create(
    65m,
    ProviderId.Claude,
    TransportId.ClaudeDirectCli,
    now).Value!;
var statusCandidate = new LimitWindowCandidate(
    LimitPeriod.FiveHours,
    claudeStatusRemaining,
    now.AddHours(2),
    new ObservationCursor(0, 1, now, "1"),
    new DataProvenance(TransportId.ClaudeStatusLine, now, "1"));
var directCandidate = statusCandidate with
{
    Remaining = claudeDirectRemaining,
    Provenance = new DataProvenance(TransportId.ClaudeDirectCli, now, "1")
};
var statusObservation = new ProviderObservationEnvelope(
    ProviderId.Claude,
    TransportId.ClaudeStatusLine,
    0,
    1,
    "1",
    now,
    now,
    ObservationCompleteness.Partial,
    ImmutableDictionary<LimitPeriod, LimitWindowCandidate>.Empty.Add(LimitPeriod.FiveHours, statusCandidate),
    EffectId.New());
var statusMerge = ObservationMergePolicy.TryMerge(
    ProviderState.Initial(ProviderId.Claude),
    statusObservation,
    now);
var claudeWithStatus = ProviderState.Initial(ProviderId.Claude) with { LastKnownGood = statusMerge.Value };
var directObservation = statusObservation with
{
    Transport = TransportId.ClaudeDirectCli,
    Windows = ImmutableDictionary<LimitPeriod, LimitWindowCandidate>.Empty.Add(LimitPeriod.FiveHours, directCandidate)
};
var directMerge = ObservationMergePolicy.TryMerge(claudeWithStatus, directObservation, now);
AssertEqual(65m, directMerge.Value!.Windows[LimitPeriod.FiveHours].Remaining.Value, "T-008 direct wins at equal captured time");
var staleStatusMerge = ObservationMergePolicy.TryMerge(
    claudeWithStatus with { LastKnownGood = directMerge.Value },
    statusObservation with
    {
        CapturedAtUtc = now.AddMinutes(-1),
        Windows = ImmutableDictionary<LimitPeriod, LimitWindowCandidate>.Empty.Add(
            LimitPeriod.FiveHours,
            statusCandidate with
            {
                Cursor = statusCandidate.Cursor with { CapturedAtUtc = now.AddMinutes(-1) },
                Provenance = statusCandidate.Provenance with { CapturedAtUtc = now.AddMinutes(-1) }
            })
    },
    now);
AssertEqual(65m, staleStatusMerge.Value!.Windows[LimitPeriod.FiveHours].Remaining.Value, "T-009 old statusLine cannot overwrite direct");

var pushed = AppReducer.Reduce(
    AppState.Empty,
    new ObservationReceivedCommand(ProviderId.Claude, statusObservation, now, Guid.NewGuid()));
AssertEqual(
    70m,
    pushed.State.Providers[ProviderId.Claude].LastKnownGood!.Windows[LimitPeriod.FiveHours].Remaining.Value,
    "T-003 statusLine push enters the single-writer store");
var waitingClaude = AppState.Empty with
{
    Lifecycle = AppLifecycleState.Running,
    Providers = AppState.Empty.Providers.SetItem(
        ProviderId.Claude,
        ProviderState.Initial(ProviderId.Claude) with
        {
            Pipeline = ProviderState.Initial(ProviderId.Claude).Pipeline with { Phase = PipelinePhase.Waiting }
        })
};
var statusLineDeferred = AppReducer.Reduce(
    waitingClaude,
    new ObservationReceivedCommand(ProviderId.Claude, statusObservation, now, Guid.NewGuid()));
var reconciliationWake = statusLineDeferred.Effects.OfType<ScheduleWakeEffect>().Single();
AssertEqual(
    now.AddMinutes(15),
    reconciliationWake.DueAtUtc,
    "T-013 statusLine defers expensive Claude direct reconciliation");
AssertEqual(
    PipelinePhase.Waiting,
    statusLineDeferred.State.Providers[ProviderId.Claude].Pipeline.Phase,
    "T-013 statusLine keeps Claude pipeline waiting");
var staleReconciliationWake = AppReducer.Reduce(
    statusLineDeferred.State,
    new WakeElapsedCommand(ProviderId.Claude, WakeId.New(), now.AddMinutes(5), Guid.NewGuid()));
AssertEqual(
    statusLineDeferred.State.Revision,
    staleReconciliationWake.State.Revision,
    "T-013 stale direct wake is ignored after statusLine push");
var invalidPush = statusObservation with
{
    Windows = ImmutableDictionary<LimitPeriod, LimitWindowCandidate>.Empty.Add(
        LimitPeriod.FiveHours,
        statusCandidate with { ResetAtUtc = now.AddHours(-2) })
};
var rejectedPush = AppReducer.Reduce(
    pushed.State,
    new ObservationReceivedCommand(ProviderId.Claude, invalidPush, now, Guid.NewGuid()));
AssertEqual(
    TransportHealth.Degraded,
    rejectedPush.State.Providers[ProviderId.Claude].Transports[TransportId.ClaudeStatusLine].Health,
    "T-001 invalid statusLine only degrades statusLine transport");
AssertEqual(
    TransportHealth.Unknown,
    rejectedPush.State.Providers[ProviderId.Claude].Transports[TransportId.ClaudeDirectCli].Health,
    "T-001 invalid statusLine leaves direct transport isolated");

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

Console.WriteLine("Domain M1/M5/M6: all cases passed.");
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
