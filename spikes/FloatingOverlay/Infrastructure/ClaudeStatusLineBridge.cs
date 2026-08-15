using System.IO;
using System.Text.Json;

namespace LLMLimitsWidget.FloatingOverlay;

public static class ClaudeStatusLineSnapshotPath
{
    public static string Default => Environment.GetEnvironmentVariable("LLM_LIMITS_CLAUDE_SNAPSHOT")
        ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LLMLimitsWidget",
        "claude-statusline-snapshot.json");
}

/// <summary>
/// Entrypoint used by Claude Code's statusLine command. It intentionally never
/// fails the statusLine itself: malformed or partial input is ignored.
/// </summary>
public static class ClaudeStatusLineBridge
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(
        TextReader input,
        string? snapshotPath = null,
        CancellationToken cancellationToken = default)
    {
        WidgetLogger.Initialize();
        try
        {
            var json = await input.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                WidgetLogger.Debug("ClaudeStatusLine", "empty_input");
                return 0;
            }

            var snapshot = ClaudeStatusLineParser.Parse(json, DateTimeOffset.Now);
            if (snapshot.Windows.Count == 0)
            {
                WidgetLogger.Warning("ClaudeStatusLine", "no_supported_windows");
                return 0;
            }

            var path = snapshotPath ?? ClaudeStatusLineSnapshotPath.Default;
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                WidgetLogger.Warning("ClaudeStatusLine", "snapshot_path_has_no_directory");
                return 0;
            }

            Directory.CreateDirectory(directory);
            var temporaryPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            var serialized = JsonSerializer.Serialize(snapshot, JsonOptions);
            await File.WriteAllTextAsync(temporaryPath, serialized, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
            ClaudeStatusLineUpdateSignal.Pulse();
            WidgetLogger.Debug(
                "ClaudeStatusLine",
                "snapshot_written",
                ("windowCount", snapshot.Windows.Count));
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            WidgetLogger.Warning("ClaudeStatusLine", "bridge_cancelled");
            return 0;
        }
        catch (Exception exception)
        {
            WidgetLogger.Error("ClaudeStatusLine", "bridge_failed", exception);
            return 0;
        }
    }
}

/// <summary>
/// The bridge and widget may run in separate processes. The named event gives
/// the widget a low-latency hint; the snapshot file remains the source of truth
/// and FileSystemWatcher is retained as a fallback for missed signals.
/// </summary>
internal static class ClaudeStatusLineUpdateSignal
{
    public const string EventName = "Local\\LLMLimitsWidget.ClaudeStatusLineUpdated";

    public static void Pulse()
    {
        try
        {
            using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            signal.Set();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            WidgetLogger.Debug("ClaudeStatusLine", "update_signal_unavailable");
        }
    }
}

internal static class ClaudeStatusLineParser
{
    public static ProviderLimitsSnapshot Parse(string json, DateTimeOffset observedAt)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var windows = new List<LimitWindowSnapshot>();
        if (root.TryGetProperty("rate_limits", out var rateLimits)
            && rateLimits.ValueKind == JsonValueKind.Object)
        {
            AddWindow(rateLimits, "five_hour", LimitWindowKind.FiveHour, "5h", windows);
            AddWindow(rateLimits, "seven_day", LimitWindowKind.SevenDay, "7d", windows);
        }

        return new ProviderLimitsSnapshot(
            LimitProviderId.Claude,
            observedAt,
            windows,
            windows.Count == 0 ? LimitDataStatus.Unavailable : LimitDataStatus.Fresh,
            windows.Count == 0 ? "Claude statusLine has no rate-limit windows." : null);
    }

    private static void AddWindow(
        JsonElement rateLimits,
        string propertyName,
        LimitWindowKind kind,
        string label,
        ICollection<LimitWindowSnapshot> windows)
    {
        if (!rateLimits.TryGetProperty(propertyName, out var window)
            || window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("used_percentage", out var used)
            || used.ValueKind != JsonValueKind.Number)
        {
            return;
        }

        var resetAt = window.TryGetProperty("resets_at", out var reset)
            && reset.ValueKind == JsonValueKind.Number
            && reset.TryGetInt64(out var epoch)
            ? DateTimeOffset.FromUnixTimeSeconds(epoch)
            : (DateTimeOffset?)null;
        windows.Add(new LimitWindowSnapshot(
            kind,
            label,
            Math.Clamp(100 - used.GetDouble(), 0, 100),
            resetAt));
    }
}

public sealed class ClaudeStatusLineLimitsDataSource : ILimitsDataSource
{
    private readonly string _snapshotPath;
    private readonly TimeSpan _freshnessTtl;

    public ClaudeStatusLineLimitsDataSource(
        string? snapshotPath = null,
        TimeSpan? freshnessTtl = null)
    {
        _snapshotPath = snapshotPath ?? ClaudeStatusLineSnapshotPath.Default;
        _freshnessTtl = freshnessTtl ?? TimeSpan.FromMinutes(3);
    }

    public LimitProviderId Provider => LimitProviderId.Claude;

    public string SnapshotPath => _snapshotPath;

    public async Task<ProviderLimitsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_snapshotPath))
        {
            return ProviderLimitsSnapshot.Unavailable(
                Provider,
                DateTimeOffset.Now,
                "Claude statusLine snapshot does not exist.");
        }

        try
        {
            await using var stream = File.OpenRead(_snapshotPath);
            var snapshot = await JsonSerializer.DeserializeAsync<ProviderLimitsSnapshot>(
                stream,
                new JsonSerializerOptions(JsonSerializerDefaults.Web),
                cancellationToken).ConfigureAwait(false);
            if (snapshot is null || snapshot.Provider != Provider)
            {
                throw new InvalidDataException("Claude statusLine snapshot has an invalid provider.");
            }

            var age = DateTimeOffset.Now - snapshot.ObservedAt.ToLocalTime();
            return age <= _freshnessTtl
                ? snapshot with { Status = LimitDataStatus.Fresh, ErrorMessage = null }
                : snapshot with
                {
                    Status = LimitDataStatus.Stale,
                    ErrorMessage = $"Claude statusLine snapshot is {age.TotalMinutes:0.#} minutes old."
                };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            WidgetLogger.Warning("ClaudeStatusLine", "snapshot_read_failed", exception);
            return ProviderLimitsSnapshot.Unavailable(Provider, DateTimeOffset.Now, exception.Message);
        }
    }
}

public sealed class ClaudeHybridLimitsDataSource : IForceRefreshableLimitsDataSource, ILimitsUpdateSignalSource, IAsyncDisposable
{
    private static readonly TimeSpan ActiveFallbackCooldown = TimeSpan.FromMinutes(5);
    // If Claude Code has no configured statusLine, this is the only autonomous
    // refresh path. Keep it bounded so the widget does not remain unchanged for
    // a quarter hour while still avoiding a request on every 30-second tick.
    private static readonly TimeSpan InactiveFallbackCooldown = TimeSpan.FromMinutes(5);
    private readonly ClaudeStatusLineLimitsDataSource _statusLine;
    private readonly IForceRefreshableLimitsDataSource _direct;
    private readonly object _sync = new();
    private readonly FileSystemWatcher? _snapshotWatcher;
    private readonly EventWaitHandle? _updateSignal;
    private readonly CancellationTokenSource _signalLifetime = new();
    private readonly Task? _signalLoop;
    private DateTimeOffset? _lastDirectAttempt;
    private ProviderLimitsSnapshot? _lastDirectSnapshot;

    public ClaudeHybridLimitsDataSource(
        ClaudeStatusLineLimitsDataSource? statusLine = null,
        IForceRefreshableLimitsDataSource? direct = null)
    {
        _statusLine = statusLine ?? new ClaudeStatusLineLimitsDataSource();
        _direct = direct ?? new ClaudeUsageLimitsDataSource();
        var snapshotDirectory = System.IO.Path.GetDirectoryName(_statusLine.SnapshotPath);
        if (!string.IsNullOrWhiteSpace(snapshotDirectory))
        {
            Directory.CreateDirectory(snapshotDirectory);
            _snapshotWatcher = new FileSystemWatcher(snapshotDirectory, System.IO.Path.GetFileName(_statusLine.SnapshotPath))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _snapshotWatcher.Changed += SnapshotWatcher_Changed;
            _snapshotWatcher.Created += SnapshotWatcher_Changed;
            _snapshotWatcher.Renamed += SnapshotWatcher_Renamed;
        }

        try
        {
            _updateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ClaudeStatusLineUpdateSignal.EventName);
            _signalLoop = Task.Run(WatchSignalAsync);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            WidgetLogger.Debug("Claude", "status_line_signal_listener_unavailable");
        }
    }

    public LimitProviderId Provider => LimitProviderId.Claude;

    public event EventHandler? UpdateAvailable;

    public async Task<ProviderLimitsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var statusLineSnapshot = await _statusLine.GetSnapshotAsync(cancellationToken)
            .ConfigureAwait(false);
        if (statusLineSnapshot.Status == LimitDataStatus.Fresh)
        {
            return statusLineSnapshot;
        }

        var now = DateTimeOffset.Now;
        var hasRecentSnapshot = statusLineSnapshot.Windows.Count > 0
            && now - statusLineSnapshot.ObservedAt.ToLocalTime() <= TimeSpan.FromMinutes(15);
        var cooldown = hasRecentSnapshot ? ActiveFallbackCooldown : InactiveFallbackCooldown;
        if (!CanAttemptDirect(now, cooldown))
        {
            WidgetLogger.Debug(
                "Claude",
                "direct_refresh_skipped_during_cooldown",
                ("cooldownSeconds", cooldown.TotalSeconds),
                ("statusLineStatus", statusLineSnapshot.Status));
            return SelectBestFallback(statusLineSnapshot);
        }

        return await ReadDirectWithFallbackAsync(statusLineSnapshot, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ProviderLimitsSnapshot> ForceRefreshAsync(CancellationToken cancellationToken)
    {
        MarkDirectAttempt(DateTimeOffset.Now);
        return await ReadDirectWithFallbackAsync(
                await _statusLine.GetSnapshotAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken,
                force: true)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _signalLifetime.Cancel();
        if (_signalLoop is not null)
        {
            try
            {
                await _signalLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_snapshotWatcher is not null)
        {
            _snapshotWatcher.Changed -= SnapshotWatcher_Changed;
            _snapshotWatcher.Created -= SnapshotWatcher_Changed;
            _snapshotWatcher.Renamed -= SnapshotWatcher_Renamed;
            _snapshotWatcher.Dispose();
        }
        _updateSignal?.Dispose();
        _signalLifetime.Dispose();
    }

    private async Task<ProviderLimitsSnapshot> ReadDirectWithFallbackAsync(
        ProviderLimitsSnapshot fallback,
        CancellationToken cancellationToken,
        bool force = false)
    {
        try
        {
            MarkDirectAttempt(DateTimeOffset.Now);
            var snapshot = force
                ? await _direct.ForceRefreshAsync(cancellationToken).ConfigureAwait(false)
                : await _direct.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _lastDirectSnapshot = snapshot;
            }

            return snapshot;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            WidgetLogger.Warning(
                "Claude",
                "direct_refresh_failed",
                exception,
                ("force", force));
            var bestFallback = SelectBestFallback(fallback);
            return bestFallback.Windows.Count > 0
                ? bestFallback with { Status = LimitDataStatus.Stale, ErrorMessage = exception.Message }
                : bestFallback with { Status = LimitDataStatus.Unavailable, ErrorMessage = exception.Message };
        }
    }

    private ProviderLimitsSnapshot SelectBestFallback(ProviderLimitsSnapshot statusLineSnapshot)
    {
        ProviderLimitsSnapshot? directSnapshot;
        lock (_sync)
        {
            directSnapshot = _lastDirectSnapshot;
        }

        if (directSnapshot is not null
            && (statusLineSnapshot.Windows.Count == 0
                || directSnapshot.ObservedAt > statusLineSnapshot.ObservedAt))
        {
            return directSnapshot with
            {
                Status = LimitDataStatus.Stale,
                ErrorMessage = "Claude statusLine is unavailable; showing the last direct snapshot."
            };
        }

        return statusLineSnapshot;
    }

    private bool CanAttemptDirect(DateTimeOffset now, TimeSpan cooldown)
    {
        lock (_sync)
        {
            return !_lastDirectAttempt.HasValue || now - _lastDirectAttempt.Value >= cooldown;
        }
    }

    private void MarkDirectAttempt(DateTimeOffset now)
    {
        lock (_sync)
        {
            _lastDirectAttempt = now;
        }
    }

    private async Task WatchSignalAsync()
    {
        while (!_signalLifetime.IsCancellationRequested)
        {
            if (_updateSignal?.WaitOne(TimeSpan.FromMilliseconds(500)) == true)
            {
                RaiseUpdateAvailable();
            }
            await Task.Yield();
        }
    }

    private void SnapshotWatcher_Changed(object sender, FileSystemEventArgs e) => RaiseUpdateAvailable();

    private void SnapshotWatcher_Renamed(object sender, RenamedEventArgs e) => RaiseUpdateAvailable();

    private void RaiseUpdateAvailable()
    {
        try
        {
            UpdateAvailable?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            WidgetLogger.Error("Claude", "status_line_signal_subscriber_failed", exception);
        }
    }
}
