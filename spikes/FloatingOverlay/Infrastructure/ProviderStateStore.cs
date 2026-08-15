using System.IO;
using System.Text.Json;

namespace LLMLimitsWidget.FloatingOverlay;

/// <summary>
/// Stores only normalized, non-secret provider snapshots. A partial write can
/// never replace the previous state because the temporary file is moved only
/// after serialization has completed in the same directory.
/// </summary>
public sealed class ProviderStateStore
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly string _path;

    public ProviderStateStore(LimitProviderId provider, string? stateDirectory = null)
    {
        var directory = stateDirectory ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LLMLimitsWidget",
            "state");
        _path = System.IO.Path.Combine(directory, $"{provider.ToString().ToLowerInvariant()}-last-known-good.json");
    }

    public string Path => _path;

    public ProviderLimitsSnapshot? TryLoad()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var stored = JsonSerializer.Deserialize<StoredProviderSnapshot>(File.ReadAllText(_path), JsonOptions);
            if (stored is null || stored.SchemaVersion != SchemaVersion || stored.Snapshot is null)
            {
                return null;
            }

            return SnapshotValidation.TryNormalize(stored.Snapshot, out var snapshot, out _)
                ? snapshot with { Status = LimitDataStatus.Stale }
                : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            WidgetLogger.Warning("ProviderState", "last_known_good_read_failed", exception,
                ("path", System.IO.Path.GetFileName(_path)));
            return null;
        }
    }

    public async Task SaveAsync(ProviderLimitsSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!SnapshotValidation.TryNormalize(snapshot, out var normalized, out var reason))
        {
            throw new InvalidDataException($"Cannot persist invalid provider snapshot: {reason}");
        }

        var directory = System.IO.Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Provider state path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            var stored = new StoredProviderSnapshot(SchemaVersion, DateTimeOffset.UtcNow, normalized);
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(stored, JsonOptions),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // A future cleanup or overwrite can remove an orphaned temp file.
            }
        }
    }

    private sealed record StoredProviderSnapshot(
        int SchemaVersion,
        DateTimeOffset SavedAtUtc,
        ProviderLimitsSnapshot Snapshot);
}

public static class SnapshotValidation
{
    public static bool TryNormalize(
        ProviderLimitsSnapshot candidate,
        out ProviderLimitsSnapshot normalized,
        out string? reason)
    {
        normalized = candidate;
        reason = null;
        if (candidate.Windows.Count == 0)
        {
            reason = "no windows";
            return false;
        }

        if (candidate.Windows.GroupBy(window => window.Kind).Any(group => group.Count() > 1))
        {
            reason = "duplicate window kind";
            return false;
        }

        foreach (var window in candidate.Windows)
        {
            if (window.RemainingPercent is not { } percent || double.IsNaN(percent) || double.IsInfinity(percent))
            {
                reason = "missing or non-finite percentage";
                return false;
            }

            if (percent is < 0 or > 100)
            {
                reason = "percentage outside range";
                return false;
            }

            if (window.ResetAt is { } reset && (reset < DateTimeOffset.UnixEpoch || reset > DateTimeOffset.UtcNow.AddYears(2)))
            {
                reason = "reset timestamp outside supported range";
                return false;
            }
        }

        normalized = candidate with
        {
            ObservedAt = candidate.ObservedAt == default ? DateTimeOffset.UtcNow : candidate.ObservedAt,
            Status = LimitDataStatus.Fresh,
            ErrorMessage = null,
            Windows = candidate.Windows.Select(window => window with
            {
                RemainingPercent = Math.Clamp(window.RemainingPercent!.Value, 0, 100),
                Status = LimitDataStatus.Fresh,
                ErrorMessage = null
            }).ToArray()
        };
        return true;
    }
}
