using System.IO;
using System.Windows.Threading;
using LLMLimitsWidget.Application;
using LLMLimitsWidget.Domain;
using LLMLimitsWidget.Infrastructure.Windows;
using LLMLimitsWidget.Presentation;

namespace LLMLimitsWidget.FloatingOverlay;

/// <summary>
/// Feature-flagged WPF composition root for the new architecture. It owns the
/// bridge between background Store notifications and the WPF Dispatcher, while
/// keeping the current legacy coordinator available as rollback path.
/// </summary>
public sealed class ArchitectureV2CompositionRoot : IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AppStore _store;
    private readonly ProviderEffectExecutor _effects;
    private readonly ProviderPipelineRuntime _codexRuntime;
    private readonly ProviderPipelineRuntime _claudeRuntime;
    private readonly ClaudeStatusLineSignalPump _statusLinePump;
    private readonly IProviderCache _cache;
    private readonly ClaudeStatusLineConfigurator _statusLineConfigurator;
    private bool _started;

    private ArchitectureV2CompositionRoot(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _clock = TimeProvider.System;
        ViewModel = new WidgetViewModel();

        var deferredEffects = new DeferredEffectExecutor();
        _store = new AppStore(deferredEffects);
        var runner = new WindowsHiddenProcessRunner();
        var codexSession = new CodexAppServerSession(ProviderExecutableLocator.ResolveCodex(), _clock);
        _codexRuntime = new ProviderPipelineRuntime(
            new CodexAppServerTransport(codexSession, _clock),
            _store,
            _clock);
        _claudeRuntime = new ProviderPipelineRuntime(
            new ClaudeDirectCliTransport(ProviderExecutableLocator.ResolveClaude(), runner, _clock),
            _store,
            _clock);
        _cache = new JsonProviderCache();
        _statusLineConfigurator = new ClaudeStatusLineConfigurator();
        _effects = new ProviderEffectExecutor([_codexRuntime, _claudeRuntime], _store, _cache);
        deferredEffects.Set(_effects);
        _statusLinePump = new ClaudeStatusLineSignalPump(
            ResolveStatusLinePath(),
            _store,
            _clock);
        _store.StateChanged += Store_StateChanged;
    }

    public static bool IsEnabled(string[] arguments)
    {
        if (arguments.Any(argument => string.Equals(argument, "--legacy", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (string.Equals(
                Environment.GetEnvironmentVariable("LLM_WIDGET_LEGACY"),
                "1",
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                Environment.GetEnvironmentVariable("LLM_WIDGET_ARCH_V2"),
                "0",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public WidgetViewModel ViewModel { get; }
    public AppState CurrentState => _store.Current;

    public static ArchitectureV2CompositionRoot Create(Dispatcher dispatcher) =>
        new(dispatcher);

    public async Task StartAsync()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _store.Start(_lifetime.Token);
        await RestoreCacheAsync().ConfigureAwait(false);
        ConfigureClaudeStatusLine();
        await _codexRuntime.StartAsync(_lifetime.Token).ConfigureAwait(false);
        await _claudeRuntime.StartAsync(_lifetime.Token).ConfigureAwait(false);
        _statusLinePump.Start(_lifetime.Token);
        await _store.DispatchAsync(
            new StartApplicationCommand(_clock.GetUtcNow(), Guid.NewGuid()),
            priority: true).ConfigureAwait(false);
    }

    public async Task RequestManualRefreshAsync()
    {
        foreach (var provider in Enum.GetValues<ProviderId>())
        {
            await _store.DispatchAsync(
                new RequestProviderRefreshCommand(
                    provider,
                    RefreshReason.Manual,
                    _clock.GetUtcNow(),
                    Guid.NewGuid()),
                priority: true).ConfigureAwait(false);
        }
    }

    public ClaudeStatusLineConfigurationResult ConfigureClaudeStatusLine()
    {
        var result = _statusLineConfigurator.EnsureConfigured(ResolveBridgeExecutablePath());
        WidgetLogger.Info(
            "ClaudeStatusLine",
            "configuration_checked",
            ("state", result.State),
            ("settingsPath", result.SettingsPath));
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_started)
        {
            _lifetime.Dispose();
            return;
        }

        _store.StateChanged -= Store_StateChanged;
        try
        {
            await _statusLinePump.DisposeAsync().ConfigureAwait(false);
            await _store.DispatchAsync(
                new StopApplicationCommand(_clock.GetUtcNow(), Guid.NewGuid()),
                priority: true).ConfigureAwait(false);
            await _store.WaitForStateAsync(
                state => state.Lifecycle == AppLifecycleState.Stopped,
                TimeSpan.FromSeconds(5),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await _effects.DisposeAsync().ConfigureAwait(false);
            await _store.DisposeAsync().ConfigureAwait(false);
            _lifetime.Dispose();
            _started = false;
        }
    }

    private void Store_StateChanged(AppState state, DomainTransition transition)
    {
        var codex = state.Providers[ProviderId.Codex];
        var claude = state.Providers[ProviderId.Claude];
        WidgetLogger.Debug(
            "ArchitectureV2",
            "state_changed",
            ("revision", state.Revision),
            ("transition", transition.Events.OfType<StateTransitionEvent>().FirstOrDefault()?.Name ?? "unknown"),
            ("codexFreshness", codex.Freshness),
            ("codexHealth", codex.AggregateHealth),
            ("codexWindows", codex.LastKnownGood?.Windows.Count ?? 0),
            ("claudeFreshness", claude.Freshness),
            ("claudeHealth", claude.AggregateHealth),
            ("claudeWindows", claude.LastKnownGood?.Windows.Count ?? 0),
            ("codexTransportError", codex.Transports.Values.Select(transport => transport.LastError?.Code.ToString()).FirstOrDefault(value => value is not null) ?? string.Empty),
            ("claudeTransportError", claude.Transports.Values.Select(transport => transport.LastError?.Code.ToString()).FirstOrDefault(value => value is not null) ?? string.Empty));

        void Apply()
        {
            ViewModel.Apply(state, _clock.GetUtcNow());
        }

        if (_dispatcher.CheckAccess())
        {
            Apply();
        }
        else
        {
            _ = _dispatcher.InvokeAsync(Apply);
        }
    }

    private static string ResolveStatusLinePath() =>
        Environment.GetEnvironmentVariable("LLM_LIMITS_CLAUDE_SNAPSHOT")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LLMLimitsWidget",
            "claude-statusline-snapshot.json");

    private static string ResolveBridgeExecutablePath() =>
        Environment.GetEnvironmentVariable("LLM_LIMITS_CLAUDE_STATUSLINE_BRIDGE_PATH")
        ?? Path.Combine(
            AppContext.BaseDirectory,
            "claude-statusline-bridge",
            "LLMLimitsWidget.ClaudeStatusLineBridge.exe");

    private async Task RestoreCacheAsync()
    {
        foreach (var provider in Enum.GetValues<ProviderId>())
        {
            var nowUtc = _clock.GetUtcNow();
            try
            {
                var limits = await _cache.LoadAsync(provider, _lifetime.Token).ConfigureAwait(false);
                if (limits is not null)
                {
                    await _store.DispatchAsync(
                        new RestoreProviderCacheCommand(provider, limits, nowUtc, Guid.NewGuid()),
                        priority: true,
                        cancellationToken: _lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                await _store.DispatchAsync(
                    new ProviderCacheReadFailedCommand(
                        provider,
                        new PersistenceError(provider, ErrorCode.CacheReadFailed, "cache_read_failed", nowUtc),
                        nowUtc,
                        Guid.NewGuid()),
                    priority: true,
                    cancellationToken: _lifetime.Token).ConfigureAwait(false);
            }
        }
    }

    private sealed class DeferredEffectExecutor : IApplicationEffectExecutor
    {
        private IApplicationEffectExecutor? _inner;

        public void Set(IApplicationEffectExecutor inner) => _inner = inner;

        public ValueTask ExecuteAsync(DomainEffect effect, CancellationToken cancellationToken = default) =>
            (_inner ?? throw new InvalidOperationException("Architecture v2 effects are not composed."))
            .ExecuteAsync(effect, cancellationToken);
    }
}
