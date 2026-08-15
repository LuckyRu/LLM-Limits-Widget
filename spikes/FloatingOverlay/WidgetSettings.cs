using System.IO;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMLimitsWidget.FloatingOverlay;

public sealed class WidgetSettings
{
    public const int CurrentSchemaVersion = 4;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public LayoutOrientation Orientation { get; set; } = LayoutOrientation.Vertical;
    public double Scale { get; set; } = 1.0;
    public double SurfaceOpacity { get; set; } = 1.0;
    public double CornerRadius { get; set; } = 18;
    public bool GhostModeEnabled { get; set; }
    public bool AutoStartEnabled { get; set; }
    public WindowPlacementSettings? Placement { get; set; }

    [JsonIgnore]
    public bool CanPersist { get; private set; } = true;

    public void Normalize()
    {
        CanPersist = SchemaVersion <= CurrentSchemaVersion;
        if (CanPersist)
        {
            SchemaVersion = CurrentSchemaVersion;
        }
        Scale = double.IsFinite(Scale) ? Math.Clamp(Scale, 0.6, 2.0) : 1.0;
        SurfaceOpacity = double.IsFinite(SurfaceOpacity) ? Math.Clamp(SurfaceOpacity, 0.2, 1.0) : 1.0;
        CornerRadius = double.IsFinite(CornerRadius) ? Math.Clamp(CornerRadius, 0, 32) : 18;
        if (Placement is not null)
        {
            Placement.RelativeX = double.IsFinite(Placement.RelativeX) ? Math.Clamp(Placement.RelativeX, 0, 1) : 1;
            Placement.RelativeY = double.IsFinite(Placement.RelativeY) ? Math.Clamp(Placement.RelativeY, 0, 1) : 1;
            Placement.IsValid &= Placement.MonitorWidth > 0 && Placement.MonitorHeight > 0;
        }
    }
}

internal static class GhostStartupPolicy
{
    public static bool ShouldRestore(
        bool persistedPreference,
        bool suppressPersistedGhost,
        bool recoveryChannelAvailable)
    {
        return persistedPreference && !suppressPersistedGhost && recoveryChannelAvailable;
    }
}

public sealed class WindowPlacementSettings
{
    public bool IsValid { get; set; }
    public string? MonitorDeviceName { get; set; }
    public string? MonitorDeviceId { get; set; }
    public string? MonitorDeviceKey { get; set; }
    public double RelativeX { get; set; } = 1;
    public double RelativeY { get; set; } = 1;
    public PlacementAnchor Anchors { get; set; }
    public uint SavedDpi { get; set; } = 96;
    public int MonitorLeft { get; set; }
    public int MonitorTop { get; set; }
    public int MonitorWidth { get; set; }
    public int MonitorHeight { get; set; }
}

[Flags]
public enum PlacementAnchor
{
    None = 0,
    Left = 1,
    Right = 2,
    Top = 4,
    Bottom = 8
}

public static class WidgetSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LLMLimitsWidget",
        "widget-settings.json");

    private static string FallbackSettingsPath => Path.Combine(
        Path.GetTempPath(),
        "LLMLimitsWidget",
        "widget-settings.json");

    public static WidgetSettings Load()
    {
        foreach (var path in new[] { SettingsPath, FallbackSettingsPath })
        {
            try
            {
                if (File.Exists(path))
                {
                    var settings = JsonSerializer.Deserialize<WidgetSettings>(
                        File.ReadAllText(path), SerializerOptions) ?? new WidgetSettings();
                    settings.Normalize();
                    return settings;
                }
            }
            catch (Exception exception) when (exception is IOException
                                              or JsonException
                                              or UnauthorizedAccessException
                                              or SecurityException)
            {
                WidgetLogger.Warning(
                    "Settings",
                    "load_failed",
                    exception,
                    ("location", path == SettingsPath ? "localAppData" : "temp"));
            }
        }

        return new WidgetSettings();
    }

    public static void Save(WidgetSettings settings)
    {
        if (TrySave(SettingsPath, settings, out var primaryFailure))
        {
            return;
        }

        if (TrySave(FallbackSettingsPath, settings, out _))
        {
            WidgetLogger.Info(
                "Settings",
                "save_fallback_used",
                ("reason", primaryFailure?.GetType().Name ?? "primary_unavailable"));
            return;
        }

        if (primaryFailure is not null)
        {
            WidgetLogger.Warning("Settings", "save_failed", primaryFailure);
        }
    }

    private static bool TrySave(
        string path,
        WidgetSettings settings,
        out Exception? failure)
    {
        failure = null;
        try
        {
            settings.Normalize();
            if (!settings.CanPersist)
            {
                return true;
            }

            var directory = Path.GetDirectoryName(path);
            if (directory is null)
            {
                return false;
            }

            Directory.CreateDirectory(directory);
            var temporaryPath = $"{path}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, SerializerOptions));
            File.Move(temporaryPath, path, true);
            return true;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or SecurityException)
        {
            failure = exception;
            return false;
        }
    }
}
