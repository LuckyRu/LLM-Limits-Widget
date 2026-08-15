using System.Diagnostics;
using System.Text.Json;
using LLMLimitsWidget.Application;
using LLMLimitsWidget.Domain;
using LLMLimitsWidget.Provider.Codex;

namespace LLMLimitsWidget.Infrastructure.Windows;

public interface ICodexAppServerSession
{
    Task<string> ReadRateLimitsAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class CodexAppServerTransport : IProviderAttemptTransport
{
    private readonly ICodexAppServerSession _session;
    private readonly TimeProvider _clock;

    public CodexAppServerTransport(ICodexAppServerSession session, TimeProvider? clock = null)
    {
        _session = session;
        _clock = clock ?? TimeProvider.System;
    }

    public ProviderId Provider => ProviderId.Codex;

    public async Task<AttemptOutcome> AcquireAsync(
        AttemptContext context,
        CancellationToken cancellationToken)
    {
        var capturedAtUtc = _clock.GetUtcNow();
        var timeout = context.DeadlineUtc - capturedAtUtc;
        if (timeout <= TimeSpan.Zero)
        {
            timeout = TimeSpan.FromSeconds(1);
        }

        string json;
        try
        {
            json = await _session.ReadRateLimitsAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (HiddenProcessException exception)
        {
            return new AttemptFailed(MapProcessError(exception, capturedAtUtc));
        }

        var parsed = CodexRateLimitsParser.Parse(
            json,
            capturedAtUtc,
            _clock.GetUtcNow(),
            context.Generation,
            context.Sequence,
            context.Effect);
        return parsed.IsSuccess
            ? new AttemptSucceeded(parsed.Observation!)
            : new AttemptFailed(parsed.Error!);
    }

    private static CodexAcquisitionError MapProcessError(
        HiddenProcessException exception,
        DateTimeOffset occurredAtUtc) =>
        new(
            exception.Kind switch
            {
                ProcessFailureKind.ExecutableNotFound => ErrorCode.ExecutableNotFound,
                ProcessFailureKind.TimedOut => ErrorCode.RequestTimeout,
                ProcessFailureKind.OutputUnavailable => ErrorCode.IoUnavailable,
                _ => ErrorCode.ProcessStartFailed
            },
            exception.Kind == ProcessFailureKind.ExecutableNotFound
                ? ErrorCategory.Configuration
                : ErrorCategory.Transient,
            exception.Kind == ProcessFailureKind.ExecutableNotFound
                ? RetryDisposition.WaitForUserAction
                : RetryDisposition.Backoff,
            exception.Kind == ProcessFailureKind.ExecutableNotFound
                ? UserAction.RepairConfiguration
                : UserAction.OpenDiagnostics,
            exception.Message,
            occurredAtUtc);
}

/// <summary>
/// Persistent Codex app-server session. Requests are serialized, the session
/// is rotated after a bounded number of reads, and every failure destroys the
/// process so a later runtime retry starts from a clean protocol stream.
/// </summary>
public sealed class CodexAppServerSession : ICodexAppServerSession, IAsyncDisposable
{
    private const int InitializeRequestId = 1;
    private const int MaxRequestsPerSession = 60;
    private static readonly TimeSpan MaxSessionLifetime = TimeSpan.FromMinutes(30);
    private readonly string _executablePath;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private DateTimeOffset _startedAtUtc;
    private int _requestCount;
    private int _nextRequestId = InitializeRequestId;
    private bool _initialized;

    public CodexAppServerSession(string executablePath, TimeProvider? clock = null)
    {
        _executablePath = executablePath;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<string> ReadRateLimitsAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await EnsureSessionAsync(timeoutSource.Token).ConfigureAwait(false);
            var process = _process ?? throw new HiddenProcessException(
                ProcessFailureKind.StartFailed,
                "Codex app-server session is unavailable.");
            var requestId = ++_nextRequestId;
            await WriteLineAsync(
                process,
                JsonSerializer.Serialize(new { id = requestId, method = "account/rateLimits/read" }),
                timeoutSource.Token).ConfigureAwait(false);
            var response = await ReadResponseAsync(process, requestId, timeoutSource.Token).ConfigureAwait(false);
            _requestCount++;
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            StopSession();
            throw new HiddenProcessException(ProcessFailureKind.TimedOut, "Codex app-server request timed out.");
        }
        catch
        {
            StopSession();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            StopSession();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task EnsureSessionAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false }
            && _initialized
            && _requestCount < MaxRequestsPerSession
            && _clock.GetUtcNow() - _startedAtUtc < MaxSessionLifetime)
        {
            return;
        }

        StopSession();
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");
        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new HiddenProcessException(ProcessFailureKind.StartFailed, "Codex app-server did not start.");
            }
        }
        catch (FileNotFoundException exception)
        {
            process.Dispose();
            throw new HiddenProcessException(
                ProcessFailureKind.ExecutableNotFound,
                $"Codex executable '{_executablePath}' was not found.",
                exception);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            process.Dispose();
            throw new HiddenProcessException(
                ProcessFailureKind.StartFailed,
                $"Codex executable '{_executablePath}' could not start.",
                exception);
        }

        _ = process.StandardError.ReadToEndAsync();
        _process = process;
        _startedAtUtc = _clock.GetUtcNow();
        _requestCount = 0;
        _nextRequestId = InitializeRequestId;
        try
        {
            await WriteLineAsync(
                process,
                JsonSerializer.Serialize(new
                {
                    id = InitializeRequestId,
                    method = "initialize",
                    @params = new
                    {
                        clientInfo = new { name = "llm-limits-widget", title = "LLM Limits Widget", version = "0.2.0" },
                        capabilities = new { experimentalApi = false }
                    }
                }),
                cancellationToken).ConfigureAwait(false);
            await WriteLineAsync(process, "{\"method\":\"initialized\",\"params\":{}}", cancellationToken)
                .ConfigureAwait(false);
            _ = await ReadResponseAsync(process, InitializeRequestId, cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        catch
        {
            StopSession();
            throw;
        }
    }

    private static async Task<string> ReadResponseAsync(
        Process process,
        int requestId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                throw new HiddenProcessException(
                    ProcessFailureKind.OutputUnavailable,
                    "Codex app-server closed stdout.");
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.Number
                    && id.GetInt32() == requestId)
                {
                    return line;
                }
            }
            catch (JsonException exception)
            {
                throw new HiddenProcessException(
                    ProcessFailureKind.OutputUnavailable,
                    "Codex app-server emitted malformed JSON.",
                    exception);
            }
        }
    }

    private static async Task WriteLineAsync(
        Process process,
        string line,
        CancellationToken cancellationToken)
    {
        try
        {
            await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            throw new HiddenProcessException(
                ProcessFailureKind.OutputUnavailable,
                "Codex app-server stdin became unavailable.",
                exception);
        }
    }

    private void StopSession()
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
            process.Dispose();
        }
    }
}
