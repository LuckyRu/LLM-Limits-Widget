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
    private bool _started;

    private ArchitectureV2CompositionRoot(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _clock = TimeProvider.System;
        ViewModel = new WidgetViewModel();

        var deferredEffects = new DeferredEffectExecutor();
        _store = new AppStore(deferredEffects);
        var runner = new WindowsHiddenProcessRunner();
        var codexSession = new CodexAppServerSession(ResolveCodexPath(), _clock);
        _codexRuntime = new ProviderPipelineRuntime(
            new CodexAppServerTransport(codexSession, _clock),
            _store,
            _clock);
        _claudeRuntime = new ProviderPipelineRuntime(
            new ClaudeDirectCliTransport(ResolveClaudePath(), runner, _clock),
            _store,
            _clock);
        _effects = new ProviderEffectExecutor([_codexRuntime, _claudeRuntime]);
        deferredEffects.Set(_effects);
        _statusLinePump = new ClaudeStatusLineSignalPump(
            ResolveStatusLinePath(),
            _store,
            _clock);
        _store.StateChanged += Store_StateChanged;
    }

    public static bool IsEnabled(string[] arguments) =>
        arguments.Any(argument => string.Equals(argument, "--arch-v2", StringComparison.OrdinalIgnoreCase))
        || string.Equals(
            Environment.GetEnvironmentVariable("LLM_WIDGET_ARCH_V2"),
            "1",
            StringComparison.OrdinalIgnoreCase);

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
        await _codexRuntime.StartAsync(_lifetime.Token).ConfigureAwait(false);
        await _claudeRuntime.StartAsync(_lifetime.Token).ConfigureAwait(false);
        _statusLinePump.Start(_lifetime.Token);
        await _store.DispatchAsync(
            new StartApplicationCommand(_clock.GetUtcNow(), Guid.NewGuid()),
            priority: true).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_started)
        {
            _lifetime.Dispose();
            return;
        }

        _store.StateChanged -= Store_StateChanged;
        await _statusLinePump.DisposeAsync().ConfigureAwait(false);
        await _effects.DisposeAsync().ConfigureAwait(false);
        await _store.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _started = false;
    }

    private void Store_StateChanged(AppState state, DomainTransition transition)
    {
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

    private static string ResolveCodexPath() =>
        Environment.GetEnvironmentVariable("CODEX_CLI_PATH") ?? "codex.exe";

    private static string ResolveClaudePath() =>
        Environment.GetEnvironmentVariable("CLAUDE_CODE_PATH") ?? "claude.exe";

    private static string ResolveStatusLinePath() =>
        Environment.GetEnvironmentVariable("LLM_LIMITS_CLAUDE_SNAPSHOT")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LLMLimitsWidget",
            "claude-statusline-snapshot.json");

    private sealed class DeferredEffectExecutor : IApplicationEffectExecutor
    {
        private IApplicationEffectExecutor? _inner;

        public void Set(IApplicationEffectExecutor inner) => _inner = inner;

        public ValueTask ExecuteAsync(DomainEffect effect, CancellationToken cancellationToken = default) =>
            (_inner ?? throw new InvalidOperationException("Architecture v2 effects are not composed."))
            .ExecuteAsync(effect, cancellationToken);
    }
}
