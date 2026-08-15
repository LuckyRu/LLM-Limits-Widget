using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace LLMLimitsWidget.FloatingOverlay;

internal enum WidgetLogLevel
{
    Debug,
    Info,
    Warning,
    Error,
    Critical
}

/// <summary>
/// Small dependency-free JSONL logger for the desktop widget.
/// It is deliberately fail-safe: logging must never affect the widget.
/// </summary>
internal static class WidgetLogger
{
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private const int MaxRotatedFilesPerDay = 5;
    private const int RetentionDays = 14;
    private const int MaxPropertyStringLength = 512;
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
    private static string? _logDirectory;
    private static bool _initialized;

    public static string LogDirectory
    {
        get
        {
            lock (Sync)
            {
                return _logDirectory ?? GetDefaultDirectory();
            }
        }
    }

    public static void Initialize()
    {
        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            _logDirectory = GetDefaultDirectory();
            TryPrepareDirectory();
            TryCleanup();
        }

        Info("App", "logger_initialized", ("logDirectory", LogDirectory));
    }

    public static void Debug(string component, string message, params (string Key, object? Value)[] properties) =>
        Write(WidgetLogLevel.Debug, component, message, null, properties);

    public static void Info(string component, string message, params (string Key, object? Value)[] properties) =>
        Write(WidgetLogLevel.Info, component, message, null, properties);

    public static void Warning(string component, string message, params (string Key, object? Value)[] properties) =>
        Write(WidgetLogLevel.Warning, component, message, null, properties);

    public static void Warning(
        string component,
        string message,
        Exception exception,
        params (string Key, object? Value)[] properties) =>
        Write(WidgetLogLevel.Warning, component, message, exception, properties);

    public static void Error(
        string component,
        string message,
        Exception? exception = null,
        params (string Key, object? Value)[] properties) =>
        Write(WidgetLogLevel.Error, component, message, exception, properties);

    public static void Critical(
        string component,
        string message,
        Exception? exception = null,
        params (string Key, object? Value)[] properties) =>
        Write(WidgetLogLevel.Critical, component, message, exception, properties);

    private static void Write(
        WidgetLogLevel level,
        string component,
        string message,
        Exception? exception,
        IReadOnlyList<(string Key, object? Value)> properties)
    {
        try
        {
            lock (Sync)
            {
                if (!_initialized)
                {
                    _initialized = true;
                    _logDirectory = GetDefaultDirectory();
                }

                if (!TryPrepareDirectory())
                {
                    return;
                }

                var entry = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["timestamp"] = DateTimeOffset.UtcNow,
                    ["level"] = level.ToString(),
                    ["component"] = component,
                    ["message"] = message,
                    ["processId"] = Environment.ProcessId,
                    ["sessionId"] = Process.GetCurrentProcess().SessionId
                };

                if (exception is not null)
                {
                    entry["exception"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["type"] = exception.GetType().FullName,
                        ["message"] = exception.Message,
                        ["stackTrace"] = exception.StackTrace
                    };
                }

                foreach (var (key, value) in properties)
                {
                    entry[key] = SanitizeValue(key, value);
                }

                var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
                var fileInfo = new FileInfo(GetActiveLogPath());
                if (fileInfo.Exists && fileInfo.Length + line.Length > MaxFileBytes)
                {
                    Rotate(fileInfo.FullName);
                }

                File.AppendAllText(fileInfo.FullName, line);
            }
        }
        catch
        {
            // Diagnostics are best-effort and must never take down the widget.
        }
    }

    private static string GetDefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LLMLimitsWidget",
        "logs");

    private static bool TryPrepareDirectory()
    {
        try
        {
            Directory.CreateDirectory(_logDirectory ?? GetDefaultDirectory());
            return true;
        }
        catch
        {
            try
            {
                _logDirectory = Path.Combine(Path.GetTempPath(), "LLMLimitsWidget", "logs");
                Directory.CreateDirectory(_logDirectory);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static string GetActiveLogPath()
    {
        var date = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        return Path.Combine(_logDirectory ?? GetDefaultDirectory(), $"widget-{date}.log");
    }

    private static void Rotate(string activePath)
    {
        for (var index = MaxRotatedFilesPerDay - 1; index >= 1; index--)
        {
            var source = $"{activePath}.{index}";
            var target = $"{activePath}.{index + 1}";
            if (File.Exists(source))
            {
                File.Move(source, target, overwrite: true);
            }
        }

        if (File.Exists(activePath))
        {
            File.Move(activePath, $"{activePath}.1", overwrite: true);
        }
    }

    private static void TryCleanup()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            foreach (var file in Directory.EnumerateFiles(
                         _logDirectory ?? GetDefaultDirectory(),
                         "widget-*.log*",
                         SearchOption.TopDirectoryOnly))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // Best effort only.
        }
    }

    private static object? SanitizeValue(string key, object? value)
    {
        if (key.Contains("token", StringComparison.OrdinalIgnoreCase)
            || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || key.Contains("password", StringComparison.OrdinalIgnoreCase)
            || key.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || key.Contains("authorization", StringComparison.OrdinalIgnoreCase)
            || key.Contains("stdin", StringComparison.OrdinalIgnoreCase)
            || key.Contains("stdout", StringComparison.OrdinalIgnoreCase)
            || key.Contains("stderr", StringComparison.OrdinalIgnoreCase))
        {
            return "[REDACTED]";
        }

        if (value is string text && text.Length > MaxPropertyStringLength)
        {
            return text[..MaxPropertyStringLength] + "…";
        }

        return value;
    }
}
