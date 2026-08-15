using System.Text.Json;

namespace LLMLimitsWidget.ClaudeStatusLineBridge;

/// <summary>
/// Small, dependency-free adapter run by Claude Code's statusLine command.
/// It retains only rate-limit data, atomically publishes it for the widget and
/// never lets an input or filesystem fault break Claude Code's own UI.
/// </summary>
public static class StatusLineBridge
{
    public const string UpdateSignalName = "Local\\LLMLimitsWidget.ClaudeStatusLineUpdated";

    public static async Task<int> RunAsync(
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var inputJson = await input.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = TryCreateSnapshot(inputJson);
            if (snapshot is null)
            {
                return 0;
            }

            var path = ResolveSnapshotPath();
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return 0;
            }

            Directory.CreateDirectory(directory);
            var temporaryPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryPath, snapshot, cancellationToken).ConfigureAwait(false);
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            PulseUpdateSignal();

            // The widget is the visual surface. Keep Claude Code's own status
            // line unobtrusive while still writing a valid successful result.
            await output.WriteAsync(" ").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            WriteDiagnostic("cancelled");
        }
        catch (Exception)
        {
            // A statusLine command must never degrade or blank Claude Code due
            // to a widget-side problem. Detailed diagnostics stay in the host.
            WriteDiagnostic("bridge_failed");
        }

        return 0;
    }

    private static string? TryCreateSnapshot(string inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(inputJson);
        if (!document.RootElement.TryGetProperty("rate_limits", out var rateLimits)
            || rateLimits.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("rate_limits");
            var count = 0;
            count += WriteWindow(writer, rateLimits, "five_hour");
            count += WriteWindow(writer, rateLimits, "seven_day");
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.Flush();
            if (count == 0)
            {
                return null;
            }
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static int WriteWindow(Utf8JsonWriter writer, JsonElement rateLimits, string name)
    {
        if (!rateLimits.TryGetProperty(name, out var window)
            || window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("used_percentage", out var used)
            || !used.TryGetDecimal(out var usedPercentage)
            || usedPercentage is < 0m or > 100m
            || !window.TryGetProperty("resets_at", out var reset)
            || !reset.TryGetInt64(out var resetAt)
            || resetAt <= 0)
        {
            return 0;
        }

        writer.WriteStartObject(name);
        writer.WriteNumber("used_percentage", usedPercentage);
        writer.WriteNumber("resets_at", resetAt);
        writer.WriteEndObject();
        return 1;
    }

    private static string ResolveSnapshotPath() =>
        Environment.GetEnvironmentVariable("LLM_LIMITS_CLAUDE_SNAPSHOT")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LLMLimitsWidget",
            "claude-statusline-snapshot.json");

    private static void PulseUpdateSignal()
    {
        try
        {
            using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, UpdateSignalName);
            signal.Set();
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            // FileSystemWatcher remains the delivery fallback.
            WriteDiagnostic("named_signal_unavailable");
        }
    }

    private static void WriteDiagnostic(string code)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LLMLimitsWidget",
                "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"claude-statusline-bridge-{DateTime.UtcNow:yyyy-MM-dd}.log");
            File.AppendAllText(
                path,
                $"{{\"timestamp\":\"{DateTime.UtcNow:O}\",\"component\":\"ClaudeStatusLineBridge\",\"code\":\"{code}\"}}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // A diagnostic sink must not affect the status line process.
        }
    }
}
