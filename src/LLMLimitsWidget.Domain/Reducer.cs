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
            ObservationReceivedCommand received => ObservationReceived(state, received),
            TransportObservationFailedCommand failed => TransportObservationFailed(state, failed),
            RestoreProviderCacheCommand restored => RestoreProviderCache(state, restored),
            ProviderCacheReadFailedCommand readFailed => CacheReadFailed(state, readFailed),
            ProviderCacheSavedCommand saved => CacheSaved(state, saved),
            ProviderCacheSaveFailedCommand saveFailed => CacheSaveFailed(state, saveFailed),
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
                Pipeline = current.Pipeline with
                {
                    Phase = PipelinePhase.Stopping,
                    ActiveAttempt = null,
                    NextWakeAtUtc = null,
                    ScheduledWake = null
                },
                NextAttemptAtUtc = null
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
        if (pipeline.Phase is PipelinePhase.Created or PipelinePhase.Starting)
        {
            var queued = pipeline with { PendingReasons = pipeline.PendingReasons.Add(command.Reason) };
            return ReplaceProvider(
                state,
                command.Provider,
                provider with { Pipeline = queued },
                [],
                command.CorrelationId,
                "refresh_queued_until_runtime_ready");
        }

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
        var reasons = new RefreshReasonSet(command.Reason).Add(pipeline.PendingReasons.Value);
        var attempt = CreateAttempt(command.Provider, provider, reasons, command.NowUtc);
        var nextProvider = provider with
        {
            Pipeline = pipeline with
            {
                Phase = phase,
                ActiveAttempt = attempt.Attempt,
                NextSequence = attempt.Sequence,
                PendingReasons = RefreshReasonSet.Empty,
                NextWakeAtUtc = null,
                ScheduledWake = null
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

        var reasons = provider.Pipeline.PendingReasons.IsEmpty
            ? new RefreshReasonSet(RefreshReason.Startup)
            : provider.Pipeline.PendingReasons.Add(RefreshReason.Startup);
        var attempt = CreateAttempt(command.Provider, provider, reasons, command.NowUtc);
        var next = provider with
        {
            Pipeline = provider.Pipeline with
            {
                Phase = PipelinePhase.Refreshing,
                ActiveAttempt = attempt.Attempt,
                NextSequence = attempt.Sequence,
                PendingReasons = RefreshReasonSet.Empty,
                NextWakeAtUtc = null,
                ScheduledWake = null
            },
            LastAttemptAtUtc = command.NowUtc
        };
        var providers = state.Providers.SetItem(command.Provider, next);
        var appState = state with
        {
            Lifecycle = providers.Values.All(item => item.Pipeline.Phase is not PipelinePhase.Created and not PipelinePhase.Starting)
                ? AppLifecycleState.Running
                : state.Lifecycle,
            Providers = providers
        };
        return Changed(
            state,
            appState,
            [new RunProviderAttemptEffect(attempt.Effect, attempt)],
            command.Provider,
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
        var dueAtUtc = command.RetryAllowed ? command.NowUtc.AddSeconds(1) : (DateTimeOffset?)null;
        var wake = command.RetryAllowed ? WakeId.New() : (WakeId?)null;
        var next = provider with
        {
            Pipeline = provider.Pipeline with
            {
                Phase = phase,
                LastLifecycleError = command.Error,
                NextWakeAtUtc = dueAtUtc,
                ScheduledWake = wake
            }
        };
        ImmutableArray<DomainEffect> effects = command.RetryAllowed
            ? [new ScheduleWakeEffect(
                EffectId.New(),
                command.Provider,
                wake!.Value,
                dueAtUtc!.Value)]
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
                LastLifecycleError = command.Error,
                ActiveAttempt = null,
                NextWakeAtUtc = null,
                ScheduledWake = null
            }
        };
        return ReplaceProvider(state, command.Provider, next, effects, command.CorrelationId, "runtime_faulted");
    }

    private static DomainTransition WakeElapsed(AppState state, WakeElapsedCommand command)
    {
        if (!state.Providers.TryGetValue(command.Provider, out var provider)
            || provider.Pipeline.ScheduledWake != command.Wake)
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
                    NextWakeAtUtc = null,
                    ScheduledWake = null
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

        if (provider.Pipeline.Phase is not (PipelinePhase.BackingOff or PipelinePhase.Waiting))
        {
            return NoChange(state);
        }

        var attempt = CreateAttempt(
            command.Provider,
            provider,
            new RefreshReasonSet(RefreshReason.Recovery),
            command.NowUtc);
        var next = provider with
        {
            Pipeline = provider.Pipeline with
            {
                Phase = PipelinePhase.Refreshing,
                ActiveAttempt = attempt.Attempt,
                NextSequence = attempt.Sequence,
                NextWakeAtUtc = null,
                ScheduledWake = null
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
                ActiveAttempt = null,
                NextWakeAtUtc = null,
                ScheduledWake = null
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
            return ApplyAttemptFailure(
                state,
                command.Provider,
                command.Context.Transport,
                failed.Error,
                command.NowUtc,
                command.CorrelationId,
                "provider_attempt_failed");
        }

        var success = (AttemptSucceeded)command.Outcome;
        var merged = ObservationMergePolicy.TryMerge(provider, success.Observation, command.NowUtc);
        if (!merged.IsSuccess)
        {
            return ApplyAttemptFailure(
                state,
                command.Provider,
                command.Context.Transport,
                merged.Error!,
                command.NowUtc,
                command.CorrelationId,
                "observation_rejected");
        }

        var accepted = merged.Value!;
        if (!HasWindowChanges(provider.LastKnownGood, accepted))
        {
            return CompleteUnchangedAttempt(state, provider, command, accepted);
        }

        var healthyTransport = provider.Transports[command.Context.Transport] with
        {
            Health = TransportHealth.Healthy,
            LastError = null,
            ConsecutiveFailures = 0,
            LastSuccessAtUtc = command.NowUtc
        };
        var pendingReasons = provider.Pipeline.PendingReasons;
        var nextAttempt = pendingReasons.IsEmpty
            ? null
            : CreateAttempt(command.Provider, provider, pendingReasons, command.NowUtc);
        var nextWake = nextAttempt is null ? WakeId.New() : (WakeId?)null;
        var nextWakeAtUtc = nextAttempt is null
            ? command.NowUtc.Add(ProviderRefreshSchedule.HealthyInterval(command.Provider))
            : (DateTimeOffset?)null;
        var updated = provider with
        {
            LastKnownGood = accepted,
            Freshness = DataFreshness.Fresh,
            AggregateHealth = ProviderHealth.Healthy,
            Pipeline = provider.Pipeline with
            {
                Phase = nextAttempt is null ? PipelinePhase.Waiting : PipelinePhase.Refreshing,
                ActiveAttempt = nextAttempt?.Attempt,
                NextSequence = nextAttempt?.Sequence ?? provider.Pipeline.NextSequence,
                PendingReasons = RefreshReasonSet.Empty,
                NextWakeAtUtc = nextWakeAtUtc,
                ScheduledWake = nextWake
            },
            Transports = provider.Transports.SetItem(command.Context.Transport, healthyTransport),
            LastSuccessAtUtc = command.NowUtc,
            AcceptedGeneration = command.Context.Generation,
            AcceptedSequence = command.Context.Sequence,
            NextAttemptAtUtc = nextWakeAtUtc
        };
        var effects = ImmutableArray.CreateBuilder<DomainEffect>();
        effects.Add(new SaveProviderCacheEffect(EffectId.New(), command.Provider, accepted));
        if (nextAttempt is not null)
        {
            effects.Add(new RunProviderAttemptEffect(nextAttempt.Effect, nextAttempt));
        }
        else
        {
            effects.Add(new ScheduleWakeEffect(
                EffectId.New(),
                command.Provider,
                nextWake!.Value,
                nextWakeAtUtc!.Value));
        }
        return ReplaceProvider(
            state,
            command.Provider,
            updated,
            effects,
            command.CorrelationId,
            "provider_observation_accepted");
    }

    private static DomainTransition ObservationReceived(
        AppState state,
        ObservationReceivedCommand command)
    {
        if (!state.Providers.TryGetValue(command.Provider, out var provider)
            || command.Observation.Provider != command.Provider
            || !provider.Transports.ContainsKey(command.Observation.Transport))
        {
            return NoChange(state);
        }

        var merged = ObservationMergePolicy.TryMerge(provider, command.Observation, command.NowUtc);
        if (!merged.IsSuccess)
        {
            var failedTransport = provider.Transports[command.Observation.Transport] with
            {
                Health = TransportHealth.Degraded,
                LastError = merged.Error,
                ConsecutiveFailures = provider.Transports[command.Observation.Transport].ConsecutiveFailures + 1,
                LastAttemptAtUtc = command.NowUtc
            };
            var failed = provider with
            {
                Transports = provider.Transports.SetItem(command.Observation.Transport, failedTransport)
            };
            return ReplaceProvider(
                state,
                command.Provider,
                failed,
                [],
                command.CorrelationId,
                "provider_observation_rejected");
        }

        var healthyTransport = provider.Transports[command.Observation.Transport] with
        {
            Health = TransportHealth.Healthy,
            LastError = null,
            ConsecutiveFailures = 0,
            LastSuccessAtUtc = command.NowUtc
        };
        var shouldDeferClaudeDirect = command.Provider == ProviderId.Claude
            && command.Observation.Transport == TransportId.ClaudeStatusLine
            && provider.Pipeline.Phase is PipelinePhase.Waiting or PipelinePhase.BackingOff;
        var reconciliationWake = shouldDeferClaudeDirect ? WakeId.New() : (WakeId?)null;
        var reconciliationDueAtUtc = shouldDeferClaudeDirect
            ? command.NowUtc.Add(ProviderRefreshSchedule.StatusLineReconciliationInterval(command.Provider))
            : (DateTimeOffset?)null;
        var updated = provider with
        {
            LastKnownGood = merged.Value,
            Freshness = DataFreshness.Fresh,
            AggregateHealth = ProviderHealth.Healthy,
            Pipeline = shouldDeferClaudeDirect
                ? provider.Pipeline with
                {
                    Phase = PipelinePhase.Waiting,
                    NextWakeAtUtc = reconciliationDueAtUtc,
                    ScheduledWake = reconciliationWake
                }
                : provider.Pipeline,
            Transports = provider.Transports.SetItem(command.Observation.Transport, healthyTransport),
            LastSuccessAtUtc = command.NowUtc,
            NextAttemptAtUtc = reconciliationDueAtUtc ?? provider.NextAttemptAtUtc
        };
        var effects = ImmutableArray.CreateBuilder<DomainEffect>();
        effects.Add(new SaveProviderCacheEffect(EffectId.New(), command.Provider, merged.Value!));
        if (reconciliationWake is { } wake && reconciliationDueAtUtc is { } dueAtUtc)
        {
            effects.Add(new ScheduleWakeEffect(EffectId.New(), command.Provider, wake, dueAtUtc));
        }
        return ReplaceProvider(
            state,
            command.Provider,
            updated,
            effects,
            command.CorrelationId,
            shouldDeferClaudeDirect
                ? "claude_statusline_reconciliation_deferred"
                : "provider_push_observation_accepted");
    }

    private static DomainTransition ApplyAttemptFailure(
        AppState state,
        ProviderId providerId,
        TransportId transportId,
        DomainError error,
        DateTimeOffset nowUtc,
        Guid correlationId,
        string eventName)
    {
        var provider = state.Providers[providerId];
        var currentTransport = provider.Transports.TryGetValue(transportId, out var knownTransport)
            ? knownTransport
            : TransportState.Initial(transportId);
        var failures = currentTransport.ConsecutiveFailures + 1;
        var nextTransport = currentTransport with
        {
            Health = error.Retry is RetryDisposition.WaitForUserAction or RetryDisposition.WaitForVersionChange or RetryDisposition.Never
                ? TransportHealth.ActionRequired
                : TransportHealth.Degraded,
            LastError = error,
            ConsecutiveFailures = failures,
            LastAttemptAtUtc = nowUtc
        };

        var phase = error.Retry switch
        {
            RetryDisposition.Immediate or RetryDisposition.Backoff => PipelinePhase.BackingOff,
            RetryDisposition.WaitForSignal => PipelinePhase.Waiting,
            RetryDisposition.Never => PipelinePhase.Faulted,
            _ => PipelinePhase.ActionRequired
        };
        var retryDelay = ProviderRefreshSchedule.RetryDelay(error.Retry, failures);
        var dueAtUtc = retryDelay == Timeout.InfiniteTimeSpan ? (DateTimeOffset?)null : nowUtc.Add(retryDelay);
        var wake = dueAtUtc is null ? (WakeId?)null : WakeId.New();
        var nextProvider = provider with
        {
            Freshness = provider.LastKnownGood is null ? DataFreshness.Missing : DataFreshness.Stale,
            AggregateHealth = phase == PipelinePhase.ActionRequired
                ? ProviderHealth.ActionRequired
                : phase == PipelinePhase.Faulted
                    ? ProviderHealth.Faulted
                    : ProviderHealth.Degraded,
            Pipeline = provider.Pipeline with
            {
                Phase = phase,
                ActiveAttempt = null,
                NextWakeAtUtc = dueAtUtc,
                ScheduledWake = wake
            },
            Transports = provider.Transports.SetItem(transportId, nextTransport),
            NextAttemptAtUtc = dueAtUtc
        };
        ImmutableArray<DomainEffect> effects = dueAtUtc is { } due && wake is { } scheduledWake
            ? [new ScheduleWakeEffect(EffectId.New(), providerId, scheduledWake, due)]
            : [];
        return ReplaceProvider(state, providerId, nextProvider, effects, correlationId, eventName);
    }

    private static DomainTransition CompleteUnchangedAttempt(
        AppState state,
        ProviderState provider,
        AttemptCompletedCommand command,
        ProviderLimits accepted)
    {
        var pendingReasons = provider.Pipeline.PendingReasons;
        var nextAttempt = pendingReasons.IsEmpty
            ? null
            : CreateAttempt(command.Provider, provider, pendingReasons, command.NowUtc);
        var dueAtUtc = nextAttempt is null
            ? command.NowUtc.Add(ProviderRefreshSchedule.HealthyInterval(command.Provider))
            : (DateTimeOffset?)null;
        var wake = nextAttempt is null ? WakeId.New() : (WakeId?)null;
        var transport = provider.Transports[command.Context.Transport] with
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
            Pipeline = provider.Pipeline with
            {
                Phase = nextAttempt is null ? PipelinePhase.Waiting : PipelinePhase.Refreshing,
                ActiveAttempt = nextAttempt?.Attempt,
                NextSequence = nextAttempt?.Sequence ?? provider.Pipeline.NextSequence,
                PendingReasons = RefreshReasonSet.Empty,
                NextWakeAtUtc = dueAtUtc,
                ScheduledWake = wake
            },
            Transports = provider.Transports.SetItem(command.Context.Transport, transport),
            LastSuccessAtUtc = command.NowUtc,
            AcceptedGeneration = command.Context.Generation,
            AcceptedSequence = command.Context.Sequence,
            NextAttemptAtUtc = dueAtUtc
        };
        var effects = nextAttempt is null
            ? ImmutableArray.Create<DomainEffect>(
                new ScheduleWakeEffect(EffectId.New(), command.Provider, wake!.Value, dueAtUtc!.Value))
            : ImmutableArray.Create<DomainEffect>(new RunProviderAttemptEffect(nextAttempt.Effect, nextAttempt));
        return ReplaceProvider(
            state,
            command.Provider,
            updated,
            effects,
            command.CorrelationId,
            "provider_observation_unchanged");
    }

    private static DomainTransition TransportObservationFailed(
        AppState state,
        TransportObservationFailedCommand command)
    {
        if (!state.Providers.TryGetValue(command.Provider, out var provider)
            || !provider.Transports.TryGetValue(command.Transport, out var transport))
        {
            return NoChange(state);
        }

        var updatedTransport = transport with
        {
            Health = command.Error.Retry is RetryDisposition.WaitForUserAction or RetryDisposition.WaitForVersionChange or RetryDisposition.Never
                ? TransportHealth.ActionRequired
                : TransportHealth.Degraded,
            LastError = command.Error,
            ConsecutiveFailures = transport.ConsecutiveFailures + 1,
            LastAttemptAtUtc = command.NowUtc
        };
        var transports = provider.Transports.SetItem(command.Transport, updatedTransport);
        var hasHealthyTransport = transports.Values.Any(value => value.Health == TransportHealth.Healthy);
        var updated = provider with
        {
            Transports = transports,
            AggregateHealth = hasHealthyTransport
                ? ProviderHealth.Healthy
                : updatedTransport.Health == TransportHealth.ActionRequired
                    ? ProviderHealth.ActionRequired
                    : ProviderHealth.Degraded
        };
        return ReplaceProvider(
            state,
            command.Provider,
            updated,
            [],
            command.CorrelationId,
            "transport_observation_failed");
    }

    private static DomainTransition RestoreProviderCache(
        AppState state,
        RestoreProviderCacheCommand command)
    {
        if (!state.Providers.TryGetValue(command.Provider, out var provider)
            || command.Limits.Provider != command.Provider)
        {
            return NoChange(state);
        }

        var freshness = command.NowUtc - command.Limits.ObservedAtUtc <= ProviderRefreshSchedule.HealthyInterval(command.Provider)
            ? DataFreshness.Aging
            : DataFreshness.Stale;
        var updated = provider with
        {
            LastKnownGood = command.Limits,
            Freshness = freshness,
            Persistence = provider.Persistence with
            {
                Health = PersistenceHealth.Healthy,
                LastError = null,
                LastReadAtUtc = command.NowUtc
            }
        };
        return ReplaceProvider(state, command.Provider, updated, [], command.CorrelationId, "provider_cache_restored");
    }

    private static DomainTransition CacheReadFailed(
        AppState state,
        ProviderCacheReadFailedCommand command) =>
        UpdatePersistence(state, command.Provider, command.Error, command.NowUtc, read: true, command.CorrelationId, "provider_cache_read_failed");

    private static DomainTransition CacheSaved(
        AppState state,
        ProviderCacheSavedCommand command) =>
        UpdatePersistence(state, command.Provider, null, command.NowUtc, read: false, command.CorrelationId, "provider_cache_saved");

    private static DomainTransition CacheSaveFailed(
        AppState state,
        ProviderCacheSaveFailedCommand command) =>
        UpdatePersistence(state, command.Provider, command.Error, command.NowUtc, read: false, command.CorrelationId, "provider_cache_save_failed");

    private static DomainTransition UpdatePersistence(
        AppState state,
        ProviderId providerId,
        PersistenceError? error,
        DateTimeOffset nowUtc,
        bool read,
        Guid correlationId,
        string eventName)
    {
        if (!state.Providers.TryGetValue(providerId, out var provider))
        {
            return NoChange(state);
        }

        var persistence = provider.Persistence with
        {
            Health = error is null ? PersistenceHealth.Healthy : PersistenceHealth.Degraded,
            LastError = error,
            LastReadAtUtc = read ? nowUtc : provider.Persistence.LastReadAtUtc,
            LastWriteAtUtc = read ? provider.Persistence.LastWriteAtUtc : nowUtc
        };
        return ReplaceProvider(
            state,
            providerId,
            provider with { Persistence = persistence },
            [],
            correlationId,
            eventName);
    }

    private static bool HasWindowChanges(ProviderLimits? existing, ProviderLimits candidate)
    {
        if (existing is null || existing.Windows.Count != candidate.Windows.Count)
        {
            return true;
        }

        return candidate.Windows.Any(pair => !existing.Windows.TryGetValue(pair.Key, out var current)
            || current != pair.Value);
    }

    private static AttemptContext CreateAttempt(
        ProviderId provider,
        ProviderState current,
        RefreshReasonSet reasons,
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
            reasons,
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

public static class ObservationMergePolicy
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
                && !IsCandidateNewer(current.Provider, candidate, existing))
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

    private static bool IsCandidateNewer(
        ProviderId provider,
        LimitWindowCandidate candidate,
        LimitWindow existing)
    {
        var capturedComparison = candidate.Cursor.CapturedAtUtc.CompareTo(existing.Cursor.CapturedAtUtc);
        if (capturedComparison != 0)
        {
            return capturedComparison > 0;
        }

        var revisionComparison = CompareSourceRevision(
            candidate.Provenance.SourceRevision,
            existing.Provenance.SourceRevision);
        if (revisionComparison != 0)
        {
            return revisionComparison > 0;
        }

        return SourcePriority(provider, candidate.Provenance.Transport)
            > SourcePriority(provider, existing.Provenance.Transport);
    }

    private static int CompareSourceRevision(string? candidate, string? existing)
    {
        if (candidate is null || existing is null)
        {
            return 0;
        }

        if (long.TryParse(candidate, out var candidateNumber)
            && long.TryParse(existing, out var existingNumber))
        {
            return candidateNumber.CompareTo(existingNumber);
        }

        return string.CompareOrdinal(candidate, existing);
    }

    private static int SourcePriority(ProviderId provider, TransportId transport) =>
        provider == ProviderId.Claude && transport == TransportId.ClaudeDirectCli
            ? 2
            : 1;
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
