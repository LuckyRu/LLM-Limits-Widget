using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace LLMLimitsWidget.FloatingOverlay;

/// <summary>
/// Serializes reads through one app-server child process. The app-server limit
/// method is intentionally treated as a capability: any protocol failure drops
/// the session, so the supervisor can retry it cleanly rather than reusing a
/// corrupted stdout stream.
/// </summary>
public sealed class CodexAppServerLimitsDataSource : IForceRefreshableLimitsDataSource, IAsyncDisposable
{
    private const int InitializeRequestId = 1;
    private const int RateLimitsRequestId = 2;
    private const int MaxRequestsPerSession = 60;
    private static readonly TimeSpan MaxSessionLifetime = TimeSpan.FromMinutes(30);
    private readonly string _executablePath;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private Process? _process;
    private DateTimeOffset _sessionStartedAt;
    private int _requestCount;
    private bool _initialized;

    public CodexAppServerLimitsDataSource(
        string? executablePath = null,
        TimeSpan? timeout = null)
    {
        _executablePath = executablePath ?? LocalExecutableLocator.ResolveCodex();
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    public LimitProviderId Provider => LimitProviderId.Codex;

    public Task<ProviderLimitsSnapshot> ForceRefreshAsync(CancellationToken cancellationToken) =>
        GetSnapshotAsync(cancellationToken);

    public async Task<ProviderLimitsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(_timeout);
            try
            {
                await EnsureSessionAsync(timeoutSource.Token).ConfigureAwait(false);
                var process = _process ?? throw new InvalidOperationException("Codex app-server session is unavailable.");
                await WriteLineAsync(process, "{\"id\":2,\"method\":\"account/rateLimits/read\"}", timeoutSource.Token)
                    .ConfigureAwait(false);
                var line = await ReadResponseAsync(process, RateLimitsRequestId, timeoutSource.Token).ConfigureAwait(false);
                var snapshot = CodexRateLimitsParser.Parse(line, DateTimeOffset.UtcNow);
                _requestCount++;
                WidgetLogger.Debug("Codex", "rate_limits_received",
                    ("status", snapshot.Status),
                    ("windowCount", snapshot.Windows.Count),
                    ("requestCount", _requestCount));
                return snapshot;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                StopSession("timeout");
                throw new TimeoutException($"Codex app-server exceeded the {_timeout.TotalSeconds:0}s timeout.");
            }
            catch
            {
                StopSession("request_failed");
                throw;
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _sessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            StopSession("disposed");
        }
        finally
        {
            _sessionGate.Release();
            _sessionGate.Dispose();
        }
    }

    private async Task EnsureSessionAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false }
            && _initialized
            && _requestCount < MaxRequestsPerSession
            && DateTimeOffset.UtcNow - _sessionStartedAt < MaxSessionLifetime)
        {
            return;
        }

        StopSession(_process is null ? "start" : "rotation");
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            Arguments = "app-server --stdio",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        startInfo.Environment["CODEX_HOME"] = string.IsNullOrWhiteSpace(codexHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : codexHome;

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Codex app-server did not start.");
        }

        _process = process;
        _sessionStartedAt = DateTimeOffset.UtcNow;
        _requestCount = 0;
        try
        {
            await WriteLineAsync(process, JsonSerializer.Serialize(new
            {
                id = InitializeRequestId,
                method = "initialize",
                @params = new
                {
                    clientInfo = new
                    {
                        name = "llm-limits-widget",
                        title = "LLM Limits Widget",
                        version = "0.2.0"
                    },
                    capabilities = new { experimentalApi = false }
                }
            }), cancellationToken).ConfigureAwait(false);
            await WriteLineAsync(process, "{\"method\":\"initialized\",\"params\":{}}", cancellationToken)
                .ConfigureAwait(false);
            // Codex' local app-server has historically accepted the initialized
            // notification immediately after initialize. Keep that ordering for
            // compatibility, then drain the matching initialize response before
            // issuing the first capability request.
            _ = await ReadResponseAsync(process, InitializeRequestId, cancellationToken).ConfigureAwait(false);
            _initialized = true;
            WidgetLogger.Info("Codex", "app_server_session_started",
                ("executable", Path.GetFileName(_executablePath)),
                ("processId", process.Id));
        }
        catch
        {
            StopSession("initialize_failed");
            throw;
        }
    }

    private static async Task<string> ReadResponseAsync(Process process, int requestId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                throw new IOException("Codex app-server closed its stdout stream.");
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.Number
                || id.GetInt32() != requestId)
            {
                continue;
            }

            return line;
        }
    }

    private static async Task WriteLineAsync(Process process, string line, CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void StopSession(string reason)
    {
        var process = _process;
        _process = null;
        _initialized = false;
        _requestCount = 0;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            WidgetLogger.Debug("Codex", "app_server_session_stopped", ("reason", reason));
            process.Dispose();
        }
    }
}

internal static class CodexRateLimitsParser
{
    public static ProviderLimitsSnapshot Parse(string json, DateTimeOffset observedAt)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException(
                error.TryGetProperty("message", out var message)
                    ? message.GetString() ?? "Codex rate limit request failed."
                    : "Codex rate limit request failed.");
        }

        if (!root.TryGetProperty("result", out var result)
            || !TryGetCodexBucket(result, out var bucket))
        {
            return new ProviderLimitsSnapshot(
                LimitProviderId.Codex,
                observedAt,
                Array.Empty<LimitWindowSnapshot>(),
                LimitDataStatus.Unavailable,
                "Codex rate-limit bucket is unavailable.");
        }

        var windows = new List<LimitWindowSnapshot>();
        AddWindow(bucket, "primary", windows);
        AddWindow(bucket, "secondary", windows);
        return new ProviderLimitsSnapshot(
            LimitProviderId.Codex,
            observedAt,
            windows,
            windows.Count == 0 ? LimitDataStatus.Unavailable : LimitDataStatus.Fresh,
            windows.Count == 0 ? "Codex returned no supported limit windows." : null);
    }

    private static bool TryGetCodexBucket(JsonElement result, out JsonElement bucket)
    {
        if (result.TryGetProperty("rateLimitsByLimitId", out var byId)
            && byId.ValueKind == JsonValueKind.Object
            && byId.TryGetProperty("codex", out bucket))
        {
            return true;
        }

        if (result.TryGetProperty("rateLimits", out var rateLimits)
            && rateLimits.ValueKind == JsonValueKind.Object
            && (!rateLimits.TryGetProperty("limitId", out var limitId)
                || string.Equals(limitId.GetString(), "codex", StringComparison.OrdinalIgnoreCase)))
        {
            bucket = rateLimits;
            return true;
        }

        bucket = default;
        return false;
    }

    private static void AddWindow(JsonElement bucket, string propertyName, ICollection<LimitWindowSnapshot> windows)
    {
        if (!bucket.TryGetProperty(propertyName, out var window)
            || window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("windowDurationMins", out var duration)
            || duration.ValueKind != JsonValueKind.Number
            || !duration.TryGetInt32(out var durationMinutes)
            || !window.TryGetProperty("usedPercent", out var usedPercent)
            || usedPercent.ValueKind != JsonValueKind.Number)
        {
            return;
        }

        var kind = durationMinutes switch
        {
            300 => LimitWindowKind.FiveHour,
            10080 => LimitWindowKind.Weekly,
            _ => (LimitWindowKind?)null
        };
        if (kind is null)
        {
            return;
        }

        var resetAt = window.TryGetProperty("resetsAt", out var reset)
            && reset.ValueKind == JsonValueKind.Number
            && reset.TryGetInt64(out var epoch)
            ? DateTimeOffset.FromUnixTimeSeconds(epoch)
            : (DateTimeOffset?)null;
        windows.Add(new LimitWindowSnapshot(
            kind.Value,
            kind == LimitWindowKind.FiveHour ? "5h" : "W",
            Math.Clamp(100 - usedPercent.GetDouble(), 0, 100),
            resetAt,
            LimitDataStatus.Fresh));
    }
}
