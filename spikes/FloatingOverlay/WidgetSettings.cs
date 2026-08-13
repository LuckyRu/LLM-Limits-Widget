using System.IO;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMLimitsWidget.FloatingOverlay;

public sealed class WidgetSettings
{
    public int SchemaVersion { get; set; } = 2;
    public LayoutOrientation Orientation { get; set; } = LayoutOrientation.Vertical;
    public double Scale { get; set; } = 1.0;
    public double SurfaceOpacity { get; set; } = 1.0;
    public double CornerRadius { get; set; } = 18;
    public WindowPlacementSettings? Placement { get; set; }

    public void Normalize()
    {
        SchemaVersion = 2;
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

    public static WidgetSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<WidgetSettings>(
                    File.ReadAllText(SettingsPath), SerializerOptions) ?? new WidgetSettings();
                settings.Normalize();
                return settings;
            }
        }
        catch (IOException)
        {
            // Fall back to defaults when the file is unavailable or locked.
        }
        catch (JsonException)
        {
            // Fall back to defaults when the file contains invalid JSON.
        }
        catch (UnauthorizedAccessException)
        {
            // Fall back to defaults when the profile directory is unavailable.
        }
        catch (SecurityException)
        {
            // Fall back to defaults when the host denies profile access.
        }

        return new WidgetSettings();
    }

    public static void Save(WidgetSettings settings)
    {
        try
        {
            settings.Normalize();
            var directory = Path.GetDirectoryName(SettingsPath);
            if (directory is null)
            {
                return;
            }

            Directory.CreateDirectory(directory);
            var temporaryPath = $"{SettingsPath}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, SerializerOptions));
            File.Move(temporaryPath, SettingsPath, true);
        }
        catch (IOException)
        {
            // Settings are best-effort and must never prevent the widget from working.
        }
        catch (UnauthorizedAccessException)
        {
            // Settings are best-effort and must never prevent the widget from working.
        }
        catch (SecurityException)
        {
            // Settings are best-effort and must never prevent the widget from working.
        }
    }
}
