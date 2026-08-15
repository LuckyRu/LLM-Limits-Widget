using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace LLMLimitsWidget.FloatingOverlay;

public sealed class CodexAppServerLimitsDataSource : IForceRefreshableLimitsDataSource
{
    private const int InitializeRequestId = 1;
    private const int RateLimitsRequestId = 2;
    private readonly string _executablePath;
    private readonly TimeSpan _timeout;

    public CodexAppServerLimitsDataSource(
        string? executablePath = null,
        TimeSpan? timeout = null)
    {
        _executablePath = executablePath ?? LocalExecutableLocator.ResolveCodex();
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    public LimitProviderId Provider => LimitProviderId.Codex;

    public Task<ProviderLimitsSnapshot> ForceRefreshAsync(CancellationToken cancellationToken)
    {
        return GetSnapshotAsync(cancellationToken);
    }

    public async Task<ProviderLimitsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            Arguments = "app-server --stdio",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");
        }

        startInfo.Environment["CODEX_HOME"] = codexHome;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Codex app-server did not start.");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);
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
                        version = "0.1.0"
                    },
                    capabilities = new { experimentalApi = false }
                }
            }), timeoutSource.Token).ConfigureAwait(false);
            await WriteLineAsync(process, "{\"method\":\"initialized\",\"params\":{}}", timeoutSource.Token)
                .ConfigureAwait(false);
            await WriteLineAsync(process, "{\"id\":2,\"method\":\"account/rateLimits/read\"}", timeoutSource.Token)
                .ConfigureAwait(false);

            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeoutSource.Token)
                    .ConfigureAwait(false);
                if (line is null)
                {
                    throw new InvalidOperationException("Codex app-server closed before rate limits arrived.");
                }

                if (!IsResponseFor(line, RateLimitsRequestId))
                {
                    continue;
                }

                return CodexRateLimitsParser.Parse(line, DateTimeOffset.Now);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Codex app-server exceeded the {_timeout.TotalSeconds:0}s timeout.");
        }
        finally
        {
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
        }
    }

    private static async Task WriteLineAsync(
        Process process,
        string line,
        CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsResponseFor(string line, int requestId)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.Number
            && id.GetInt32() == requestId;
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
        AddWindow(bucket, "primary", windows, observedAt);
        AddWindow(bucket, "secondary", windows, observedAt);
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

    private static void AddWindow(
        JsonElement bucket,
        string propertyName,
        ICollection<LimitWindowSnapshot> windows,
        DateTimeOffset observedAt)
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
        var used = usedPercent.GetDouble();
        windows.Add(new LimitWindowSnapshot(
            kind.Value,
            kind == LimitWindowKind.FiveHour ? "5h" : "W",
            Math.Clamp(100 - used, 0, 100),
            resetAt,
            LimitDataStatus.Fresh));
    }
}
