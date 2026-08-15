using Microsoft.Win32;
using System.Runtime.Versioning;

namespace LLMLimitsWidget.Infrastructure.Windows;

public interface IWindowsStartupRegistry
{
    string? Get(string valueName);
    void Set(string valueName, string command);
    void Delete(string valueName);
}

public sealed class WindowsAutoStartRegistration
{
    public const string ValueName = "LLMLimitsWidget";
    private const string ExecutableName = "LLMLimitsWidget.FloatingOverlay.exe";
    private readonly IWindowsStartupRegistry _registry;

    public WindowsAutoStartRegistration(IWindowsStartupRegistry? registry = null)
    {
        _registry = registry ?? CreateDefaultRegistry();
    }

    public AutoStartRegistrationResult SetEnabled(bool enabled, string executablePath)
    {
        try
        {
            var current = _registry.Get(ValueName);
            if (!enabled)
            {
                if (!IsWidgetCommand(current))
                {
                    return new AutoStartRegistrationResult(AutoStartRegistrationState.Disabled, "Autostart is not owned by the widget.");
                }

                _registry.Delete(ValueName);
                return new AutoStartRegistrationResult(AutoStartRegistrationState.Disabled, "Autostart disabled.");
            }

            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                return new AutoStartRegistrationResult(AutoStartRegistrationState.ExecutableUnavailable, "Widget executable is unavailable.");
            }

            var command = CreateCommand(Path.GetFullPath(executablePath));
            if (string.Equals(current, command, StringComparison.Ordinal))
            {
                return new AutoStartRegistrationResult(AutoStartRegistrationState.Enabled, "Autostart already enabled.");
            }

            _registry.Set(ValueName, command);
            return new AutoStartRegistrationResult(AutoStartRegistrationState.Enabled, "Autostart enabled.");
        }
        catch (UnauthorizedAccessException)
        {
            return new AutoStartRegistrationResult(AutoStartRegistrationState.AccessDenied, "Windows denied autostart registration.");
        }
        catch (IOException)
        {
            return new AutoStartRegistrationResult(AutoStartRegistrationState.IoFailed, "Windows autostart registration failed.");
        }
        catch (Exception)
        {
            return new AutoStartRegistrationResult(AutoStartRegistrationState.UnexpectedFailure, "Windows autostart registration failed safely.");
        }
    }

    private static bool IsWidgetCommand(string? value) =>
        value?.Contains(ExecutableName, StringComparison.OrdinalIgnoreCase) == true;

    private static string CreateCommand(string executablePath) =>
        $"\"{executablePath.Replace("\"", string.Empty, StringComparison.Ordinal)}\" --autostart";

    private static IWindowsStartupRegistry CreateDefaultRegistry()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows autostart is only available on Windows.");
        }

        return new CurrentUserStartupRegistry();
    }
}

public enum AutoStartRegistrationState
{
    Enabled,
    Disabled,
    ExecutableUnavailable,
    AccessDenied,
    IoFailed,
    UnexpectedFailure
}

public sealed record AutoStartRegistrationResult(AutoStartRegistrationState State, string Detail)
{
    public bool IsEnabled => State == AutoStartRegistrationState.Enabled;
}

[SupportedOSPlatform("windows")]
internal sealed class CurrentUserStartupRegistry : IWindowsStartupRegistry
{
    private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";

    public string? Get(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(valueName) as string;
    }

    public void Set(string valueName, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new IOException("Current user Run registry key is unavailable.");
        key.SetValue(valueName, command, RegistryValueKind.String);
    }

    public void Delete(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
