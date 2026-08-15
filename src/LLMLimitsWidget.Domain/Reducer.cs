using System.Collections.Immutable;

namespace LLMLimitsWidget.Domain;

public static class AppReducer
{
    public static DomainTransition Reduce(AppState state, DomainCommand command)
    {
        return command switch
        {
            StartApplicationCommand start => Start(state, start),
            StopApplicationCommand stop => Stop(state, stop),
            RequestProviderRefreshCommand refresh => RequestRefresh(state, refresh),
            RuntimeReadyCommand ready => RuntimeReady(state, ready),
            RuntimeStartFailedCommand startFailed => RuntimeStartFailed(state, startFailed),
            RuntimeFaultedCommand runtimeFaulted => RuntimeFaulted(state, runtimeFaulted),
            WakeElapsedCommand wake => WakeElapsed(state, wake),
            RuntimeStoppedCommand stopped => RuntimeStopped(state, stopped),
            AttemptCompletedCommand completed => AttemptCompleted(state, completed),
            _ => NoChange(state)
        };
    }

    private static DomainTransition Start(AppState state, StartApplicationCommand command)
    {
        if (state.Lifecycle is AppLifecycleState.Starting or AppLifecycleState.Running)
        {
            return NoChange(state);
        }

        var next = state with { Lifecycle = AppLifecycleState.Starting };
        var effects = Enum.GetValues<ProviderId>()
            .Select(provider => (DomainEffect)new StartRuntimeEffect(EffectId.New(), provider))
            .ToImmutableArray();
        return Changed(state, next, effects, null, command.CorrelationId, "application_starting");
    }

    private static DomainTransition Stop(AppState state, StopApplicationCommand command)
    {
        if (state.Lifecycle is AppLifecycleState.Stopping or AppLifecycleState.Stopped)
        {
            return NoChange(state);
        }

        var providers = state.Providers;
        foreach (var provider in Enum.GetValues<ProviderId>())
        {
            providers = UpdateProvider(providers, provider, current => current with
            {
                Pipeline = current.Pipeline with { Phase = PipelinePhase.Stopping }
            });
        }

        var next = state with
        {
            Lifecycle = AppLifecycleState.Stopping,
            Providers = providers
        };
        var effects = Enum.GetValues<ProviderId>()
            .Select(provider => (DomainEffect)new StopRuntimeEffect(EffectId.New(), provider, Force: false))
            .ToImmutableArray();
        return Changed(state, next, effects, null, command.CorrelationId, "application_stopping");
    }

    private static DomainTransition RequestRefresh(
        AppState state,
        RequestProviderRefreshCommand command)
    {
        if (!state.Providers.TryGetValue(command.Provider, out var provider)
            || state.Lifecycle is AppLifecycleState.Stopping or AppLifecycleState.Stopped)
        {
            return NoChange(state);
        }

        var pipeline = provider.Pipeline;
        if (pipeline.Phase == PipelinePhase.Refreshing)
        {
            var nextPipeline = pipeline with
            {
                PendingReasons = pipeline.PendingReasons.Add(command.Reason)
            };
            return ReplaceProvider(state, command.Provider, provider with { Pipeline = nextPipeline },
                [], command.CorrelationId, "refresh_queued");
        }

        if (pipeline.Phase == PipelinePhase.ActionRequired
            && command.Reason is not (RefreshReason.Manual or RefreshReason.PushSignal or RefreshReason.Recovery))
        {
            return NoChange(state);
        }

        if (pipeline.Phase == PipelinePhase.Faulted)
        {
            return ReplaceProvider(
                state,
                command.Provider,
                provider with { Pipeline = pipeline with { Phase = PipelinePhase.Starting } },
                [new RestartRuntimeEffect(EffectId.New(), command.Provider)],
                command.CorrelationId,
                "runtime_recovery_requested");
        }

        var phase = pipeline.Phase == PipelinePhase.ActionRequired
            ? PipelinePhase.HalfOpen
            : PipelinePhase.Refreshing;
        var attempt = CreateAttempt(command.Provider, provider, command.Reason, command.NowUtc);
        var nextProvider = provider with
        {
            Pipeline = pipeline with
            {
                Phase = phase,
                ActiveAttempt = attempt.Attempt,
                NextSequence = attempt.Sequence
            },
            LastAttemptAtUtc = command.NowUtc
        };
        return ReplaceProvider(
            state,
            command.Provider,
            nextProvider,
            [new RunProviderAttemptEffect(attempt.Effect, attempt)],
            command.CorrelationId,
            "provider_refresh_started");
    }

    private static DomainTransition RuntimeReady(AppState state, RuntimeReadyCommand command)
    {
        if (!state.Providers.TryGetValue(command.Provider, out var provider)
            || provider.Pipeline.Phase is PipelinePhase.Stopping or PipelinePhase.Stopped)
        {
            return NoChange(state);
        }

        if (!command.RefreshDue)
        {
            var waiting = provider with
            {
                Pipeline = provider.Pipeline with { Phase = PipelinePhase.Waiting }
            };
            return ReplaceProvider(state, command.Provider, waiting, [], command.CorrelationId, "runtime_ready_waiting");
        }

        var attempt = CreateAttempt(command.Provider, provider, RefreshReason.Startup, command.NowUtc);
        var next = provider with
        {
            Pipeline = provider.Pipeline with
            {
                Phase = PipelinePhase.Refreshing,
                ActiveAttempt = attempt.Attempt,
                NextSequence = attempt.Sequence
            },
            LastAttemptAtUtc = command.NowUtc
        };
        return ReplaceProvider(
            state,
            command.Provider,
            next,
            [new RunProviderAttemptEffect(attempt.Effect, attempt)],
            command.CorrelationId,
            "runtime_ready_refreshing");
    }

    private static DomainTransition RuntimeStartFailed(
        AppState state,
        RuntimeStartFailedCommand command)
    {
        if (!state.Providers.TryGetValue(command.Provider, out var provider))
        {
            return NoChange(state);
        }

        var phase = command.RetryAllowed
            ? PipelinePhase.RuntimeRestartBackoff
            : PipelinePhase.Faulted;
        var next = provider with
        {
            Pipeline = provider.Pipeline with
            {
                Phase = phase,
                LastLifecycleError = command.Error,
                NextWakeAtUtc = command.RetryAllowed ? command.NowUtc.AddSeconds(1) : null
            }
        };
        ImmutableArray<DomainEffect> effects = command.RetryAllowed
            ? [new ScheduleWakeEffect(
                EffectId.New(),
                command.Provider,
                WakeId.New(),
                command.NowUtc.AddSeconds(1))]
            : [];
        return ReplaceProvider(state, command.Provider, next, effects, command.CorrelationId, "runtime_start_failed");
    }

    private static DomainTransition RuntimeFaulted(
        AppState state,
        RuntimeFaultedCommand command)
    {
        if (!state.Providers.TryGetValue(command.Provider, out var provider))
        {
            return NoChange(state);
        }

        var phase = command.RestartAllowed ? PipelinePhase.Starting : PipelinePhase.Faulted;
        ImmutableArray<DomainEffect> effects = command.RestartAllowed
            ? [new RestartRuntimeEffect(EffectId.New(), command.Provider)]
            : [];
        var next = provider with
        {
            Pipeline = provider.Pipeline with
            {
                Phase = phase,
                Generation = provider.Pipeline.Generation + 1,
                LastLifecycleError = command.Error
            }
        };
        return ReplaceProvider(state, command.Provider, next, effects, command.CorrelationId, "runtime_faulted");
    }

    private static DomainTransition WakeElapsed(AppState state, WakeElapsedCommand command)
    {
        if (!state.Providers.TryGetValue(command.Provider, out var provider))
        {
            return NoChange(state);
        }

        if (provider.Pipeline.Phase == PipelinePhase.RuntimeRestartBackoff)
        {
            var starting = provider with
            {
                Pipeline = provider.Pipeline with
                {
                    Phase = PipelinePhase.Starting,
                    NextWakeAtUtc = null
                }
            };
            return ReplaceProvider(
                state,
                command.Provider,
                starting,
                [new RestartRuntimeEffect(EffectId.New(), command.Provider)],
                command.CorrelationId,
                "runtime_restart_started");
        }

        if (provider.Pipeline.Phase != PipelinePhase.BackingOff)
        {
            return NoChange(state);
        }

        var attempt = CreateAttempt(command.Provider, provider, RefreshReason.Recovery, command.NowUtc);
        var next = provider with
        {
            Pipeline = provider.Pipeline with
            {
                Phase = PipelinePhase.Refreshing,
                ActiveAttempt = attempt.Attempt,
                NextSequence = attempt.Sequence,
                NextWakeAtUtc = null
            },
            LastAttemptAtUtc = command.NowUtc
        };
        return ReplaceProvider(
            state,
            command.Provider,
            next,
            [new RunProviderAttemptEffect(attempt.Effect, attempt)],
            command.CorrelationId,
            "provider_retry_started");
    }

    private static DomainTransition RuntimeStopped(AppState state, RuntimeStoppedCommand command)
    {
        if (!state.Providers.TryGetValue(command.Provider, out var provider))
        {
            return NoChange(state);
        }

        var next = provider with
        {
            Pipeline = provider.Pipeline with
            {
                Phase = PipelinePhase.Stopped,
                ActiveAttempt = null
            }
        };
        var nextState = state with
        {
            Lifecycle = state.Providers
                .SetItem(command.Provider, next)
                .Values.All(item => item.Pipeline.Phase == PipelinePhase.Stopped)
                ? AppLifecycleState.Stopped
                : state.Lifecycle,
            Providers = state.Providers.SetItem(command.Provider, next)
        };
        return Changed(state, nextState, [], command.Provider, command.CorrelationId, "runtime_stopped");
    }

    private static DomainTransition AttemptCompleted(AppState state, AttemptCompletedCommand command)
    {
        if (!state.Providers.TryGetValue(command.Provider, out var provider)
            || provider.Pipeline.ActiveAttempt != command.Context.Attempt
            || command.Context.Generation != provider.Pipeline.Generation)
        {
            return NoChange(state);
        }

        if (command.Outcome is AttemptFailed failed)
        {
            var transport = provider.Transports.TryGetValue(command.Context.Transport, out var currentTransport)
                ? currentTransport
                : TransportState.Initial(command.Context.Transport);
            var nextTransport = transport with
            {
                Health = failed.Error.Retry == RetryDisposition.WaitForUserAction
                    ? TransportHealth.ActionRequired
                    : TransportHealth.Degraded,
                LastError = failed.Error,
                ConsecutiveFailures = transport.ConsecutiveFailures + 1,
                LastAttemptAtUtc = command.NowUtc
            };
            var nextPhase = failed.Error.Retry == RetryDisposition.WaitForUserAction
                ? PipelinePhase.ActionRequired
                : PipelinePhase.BackingOff;
            var nextProvider = provider with
            {
                Pipeline = provider.Pipeline with
                {
                    Phase = nextPhase,
                    ActiveAttempt = null,
                    NextWakeAtUtc = nextPhase == PipelinePhase.BackingOff
                        ? command.NowUtc.AddSeconds(5)
                        : null
                },
                Transports = provider.Transports.SetItem(command.Context.Transport, nextTransport)
            };
            ImmutableArray<DomainEffect> effects = nextPhase == PipelinePhase.BackingOff
                ? [new ScheduleWakeEffect(
                    EffectId.New(),
                    command.Provider,
                    WakeId.New(),
                    command.NowUtc.AddSeconds(5))]
                : [];
            return ReplaceProvider(state, command.Provider, nextProvider, effects, command.CorrelationId, "provider_attempt_failed");
        }

        var success = (AttemptSucceeded)command.Outcome;
        var merged = ObservationMerger.TryMerge(provider, success.Observation, command.NowUtc);
        if (!merged.IsSuccess)
        {
            var rejected = provider with
            {
                Pipeline = provider.Pipeline with { Phase = PipelinePhase.BackingOff, ActiveAttempt = null },
                Transports = provider.Transports.SetItem(
                    command.Context.Transport,
                    provider.Transports[command.Context.Transport] with
                    {
                        Health = TransportHealth.Degraded,
                        LastError = merged.Error,
                        ConsecutiveFailures = provider.Transports[command.Context.Transport].ConsecutiveFailures + 1
                    })
            };
            return ReplaceProvider(state, command.Provider, rejected, [], command.CorrelationId, "observation_rejected");
        }

        var accepted = merged.Value!;
        var healthyTransport = provider.Transports[command.Context.Transport] with
        {
            Health = TransportHealth.Healthy,
            LastError = null,
            ConsecutiveFailures = 0,
            LastSuccessAtUtc = command.NowUtc
        };
        var updated = provider with
        {
            LastKnownGood = accepted,
            Freshness = DataFreshness.Fresh,
            AggregateHealth = ProviderHealth.Healthy,
            Pipeline = provider.Pipeline with { Phase = PipelinePhase.Waiting, ActiveAttempt = null },
            Transports = provider.Transports.SetItem(command.Context.Transport, healthyTransport),
            LastSuccessAtUtc = command.NowUtc,
            AcceptedGeneration = command.Context.Generation,
            AcceptedSequence = command.Context.Sequence
        };
        return ReplaceProvider(
            state,
            command.Provider,
            updated,
            [new SaveProviderCacheEffect(EffectId.New(), command.Provider, accepted)],
            command.CorrelationId,
            "provider_observation_accepted");
    }

    private static AttemptContext CreateAttempt(
        ProviderId provider,
        ProviderState current,
        RefreshReason reason,
        DateTimeOffset nowUtc)
    {
        var transport = provider == ProviderId.Codex
            ? TransportId.CodexAppServer
            : TransportId.ClaudeDirectCli;
        var sequence = current.Pipeline.NextSequence + 1;
        return new AttemptContext(
            provider,
            transport,
            AttemptId.New(),
            EffectId.New(),
            current.Pipeline.Generation,
            sequence,
            new RefreshReasonSet(reason),
            nowUtc.AddSeconds(30));
    }

    private static DomainTransition ReplaceProvider(
        AppState state,
        ProviderId provider,
        ProviderState nextProvider,
        IEnumerable<DomainEffect> effects,
        Guid correlationId,
        string eventName)
    {
        var next = state with
        {
            Revision = state.Revision + 1,
            Providers = state.Providers.SetItem(provider, nextProvider)
        };
        return new DomainTransition(
            next,
            [new StateTransitionEvent(provider, next.Revision, correlationId, eventName)],
            effects.ToImmutableArray());
    }

    private static DomainTransition Changed(
        AppState before,
        AppState next,
        IEnumerable<DomainEffect> effects,
        ProviderId? provider,
        Guid correlationId,
        string eventName)
    {
        if (before == next)
        {
            return NoChange(before);
        }

        var revised = next with { Revision = before.Revision + 1 };
        return new DomainTransition(
            revised,
            [new StateTransitionEvent(provider, revised.Revision, correlationId, eventName)],
            effects.ToImmutableArray());
    }

    private static DomainTransition NoChange(AppState state) =>
        new(state, ImmutableArray<DomainEvent>.Empty, ImmutableArray<DomainEffect>.Empty);

    private static ImmutableDictionary<ProviderId, ProviderState> UpdateProvider(
        ImmutableDictionary<ProviderId, ProviderState> providers,
        ProviderId provider,
        Func<ProviderState, ProviderState> update) =>
        providers.SetItem(provider, update(providers[provider]));
}

internal static class ObservationMerger
{
    public static DomainResult<ProviderLimits> TryMerge(
        ProviderState current,
        ProviderObservationEnvelope observation,
        DateTimeOffset nowUtc)
    {
        if (observation.Provider != current.Provider)
        {
            return DomainResult<ProviderLimits>.Failure(new InvalidObservationError(
                current.Provider,
                observation.Transport,
                ErrorCode.ProviderMismatch,
                nowUtc));
        }

        var required = current.Provider == ProviderId.Codex
            ? new[] { LimitPeriod.SevenDays }
            : new[] { LimitPeriod.FiveHours, LimitPeriod.SevenDays };
        if (observation.Completeness == ObservationCompleteness.Complete
            && required.Any(period => !observation.Windows.ContainsKey(period)))
        {
            return DomainResult<ProviderLimits>.Failure(new InvalidObservationError(
                current.Provider,
                observation.Transport,
                ErrorCode.NoSupportedWindows,
                nowUtc));
        }

        var windows = current.LastKnownGood?.Windows.ToBuilder()
            ?? ImmutableDictionary.CreateBuilder<LimitPeriod, LimitWindow>();
        foreach (var candidate in observation.Windows.Values)
        {
            if (candidate.ResetAtUtc < candidate.Cursor.CapturedAtUtc.AddMinutes(-1))
            {
                return DomainResult<ProviderLimits>.Failure(new InvalidObservationError(
                    current.Provider,
                    observation.Transport,
                    ErrorCode.InvalidResetTime,
                    nowUtc));
            }

            if (windows.TryGetValue(candidate.Period, out var existing)
                && candidate.Cursor.CapturedAtUtc < existing.Cursor.CapturedAtUtc)
            {
                continue;
            }

            windows[candidate.Period] = new LimitWindow(
                candidate.Period,
                candidate.Remaining,
                candidate.ResetAtUtc,
                candidate.Cursor,
                candidate.Provenance);
        }

        if (windows.Count == 0)
        {
            return DomainResult<ProviderLimits>.Failure(new InvalidObservationError(
                current.Provider,
                observation.Transport,
                ErrorCode.NoSupportedWindows,
                nowUtc));
        }

        var observedAt = windows.Values.Max(window => window.Provenance.CapturedAtUtc);
        return DomainResult<ProviderLimits>.Success(new ProviderLimits(
            current.Provider,
            ObservationId.New(),
            observedAt,
            windows.ToImmutable()));
    }
}

internal sealed record InvalidObservationError(
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
        "invalid_observation",
        OccurredAtUtc);
