using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LLMLimitsWidget.FloatingOverlay;

public sealed class ClaudeUsageLimitsDataSource : IForceRefreshableLimitsDataSource
{
    private readonly string _executablePath;
    private readonly TimeSpan _timeout;

    public ClaudeUsageLimitsDataSource(
        string? executablePath = null,
        TimeSpan? timeout = null)
    {
        _executablePath = executablePath ?? LocalExecutableLocator.ResolveClaude();
        _timeout = timeout ?? TimeSpan.FromSeconds(45);
    }

    public LimitProviderId Provider => LimitProviderId.Claude;

    public Task<ProviderLimitsSnapshot> ForceRefreshAsync(CancellationToken cancellationToken)
    {
        return GetSnapshotAsync(cancellationToken);
    }

    public async Task<ProviderLimitsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add("/usage");
        startInfo.ArgumentList.Add("--output-format");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("--tools");
        startInfo.ArgumentList.Add(string.Empty);
        startInfo.ArgumentList.Add("--no-session-persistence");
        startInfo.ArgumentList.Add("--setting-sources");
        startInfo.ArgumentList.Add("user");
        startInfo.ArgumentList.Add("--permission-mode");
        startInfo.ArgumentList.Add("plan");

        var output = await LocalProcess.CaptureAsync(startInfo, _timeout, cancellationToken)
            .ConfigureAwait(false);
        return ClaudeUsageParser.Parse(output, DateTimeOffset.Now);
    }
}

internal static partial class ClaudeUsageParser
{
    public static ProviderLimitsSnapshot Parse(string json, DateTimeOffset observedAt)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("is_error", out var isError)
            && isError.ValueKind == JsonValueKind.True)
        {
            throw new InvalidOperationException("Claude /usage returned an error.");
        }

        if (!root.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Claude /usage returned no readable result.");
        }

        var text = result.GetString() ?? string.Empty;
        var windows = new List<LimitWindowSnapshot>();
        AddWindow(text, "session", LimitWindowKind.FiveHour, "5h", windows, observedAt);
        AddWindow(text, "week", LimitWindowKind.SevenDay, "7d", windows, observedAt);
        return new ProviderLimitsSnapshot(
            LimitProviderId.Claude,
            observedAt,
            windows,
            windows.Count == 0 ? LimitDataStatus.Unavailable : LimitDataStatus.Fresh,
            windows.Count == 0 ? "Claude /usage returned no supported limit windows." : null);
    }

    private static void AddWindow(
        string text,
        string windowName,
        LimitWindowKind kind,
        string label,
        ICollection<LimitWindowSnapshot> windows,
        DateTimeOffset observedAt)
    {
        var match = Regex.Match(
            text,
            $"Current\\s+{windowName}(?:\\s+\\(all models\\))?\\s*:\\s*(?<used>[0-9]+(?:\\.[0-9]+)?)%\\s+used\\s*[·•]\\s*resets\\s+(?<reset>[^\\r\\n(]+)\\s*\\((?<zone>[^)]+)\\)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success
            || !double.TryParse(
                match.Groups["used"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var used))
        {
            return;
        }

        windows.Add(new LimitWindowSnapshot(
            kind,
            label,
            Math.Clamp(100 - used, 0, 100),
            ParseResetAt(match.Groups["reset"].Value.Trim(), match.Groups["zone"].Value.Trim(), observedAt),
            LimitDataStatus.Fresh));
    }

    private static DateTimeOffset? ParseResetAt(
        string value,
        string zoneName,
        DateTimeOffset observedAt)
    {
        var commaIndex = value.IndexOf(',');
        var withYear = commaIndex >= 0
            ? value.Insert(commaIndex, $" {observedAt.Year}")
            : $"{value} {observedAt.Year}";
        if (!DateTime.TryParse(
                withYear,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var localTime))
        {
            return null;
        }

        localTime = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);
        var zone = FindTimeZone(zoneName);
        var offset = zone.GetUtcOffset(localTime);
        var resetAt = new DateTimeOffset(localTime, offset);
        if (resetAt < observedAt.AddHours(-1))
        {
            var nextYear = localTime.AddYears(1);
            resetAt = new DateTimeOffset(nextYear, zone.GetUtcOffset(nextYear));
        }

        return resetAt;
    }

    private static TimeZoneInfo FindTimeZone(string zoneName)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(zoneName);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }
}
