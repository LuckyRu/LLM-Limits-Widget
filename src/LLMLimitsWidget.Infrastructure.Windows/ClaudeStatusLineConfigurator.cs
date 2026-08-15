using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLMLimitsWidget.Infrastructure.Windows;

/// <summary>
/// Safely provisions the optional low-latency Claude Code statusLine bridge.
/// It owns only the widget's exact command and refuses to overwrite another
/// status line chosen by the user.
/// </summary>
public sealed class ClaudeStatusLineConfigurator
{
    private const string BridgeExecutableName = "LLMLimitsWidget.ClaudeStatusLineBridge.exe";
    private readonly string _settingsPath;

    public ClaudeStatusLineConfigurator(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "settings.json");
    }

    public ClaudeStatusLineConfigurationResult EnsureConfigured(string bridgeExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(bridgeExecutablePath)
            || !File.Exists(bridgeExecutablePath))
        {
            return new ClaudeStatusLineConfigurationResult(
                ClaudeStatusLineConfigurationState.BridgeUnavailable,
                "Claude statusLine bridge executable is unavailable.",
                _settingsPath);
        }

        try
        {
            var root = ReadSettings();
            var existing = root["statusLine"];
            if (existing is not null && !IsWidgetManaged(existing))
            {
                return new ClaudeStatusLineConfigurationResult(
                    ClaudeStatusLineConfigurationState.ExistingUserStatusLine,
                    "A user-managed Claude statusLine already exists; it was not changed.",
                    _settingsPath);
            }

            var command = CreateCommand(Path.GetFullPath(bridgeExecutablePath));
            var isCurrent = existing is JsonObject existingObject
                && string.Equals(TryGetString(existingObject["command"]), command, StringComparison.Ordinal)
                && string.Equals(TryGetString(existingObject["type"]), "command", StringComparison.Ordinal);
            if (isCurrent)
            {
                return new ClaudeStatusLineConfigurationResult(
                    ClaudeStatusLineConfigurationState.AlreadyConfigured,
                    "Claude fast updates are already configured.",
                    _settingsPath);
            }

            root["statusLine"] = new JsonObject
            {
                ["type"] = "command",
                ["command"] = command
            };
            WriteSettingsAtomically(root);
            return new ClaudeStatusLineConfigurationResult(
                ClaudeStatusLineConfigurationState.Configured,
                "Claude fast updates were configured. Restart or use Claude Code once to activate the statusLine.",
                _settingsPath);
        }
        catch (JsonException)
        {
            return new ClaudeStatusLineConfigurationResult(
                ClaudeStatusLineConfigurationState.InvalidSettings,
                "Claude settings.json is not valid JSON; it was not changed.",
                _settingsPath);
        }
        catch (UnauthorizedAccessException)
        {
            return new ClaudeStatusLineConfigurationResult(
                ClaudeStatusLineConfigurationState.AccessDenied,
                "Claude settings.json is not accessible; it was not changed.",
                _settingsPath);
        }
        catch (IOException)
        {
            return new ClaudeStatusLineConfigurationResult(
                ClaudeStatusLineConfigurationState.IoFailed,
                "Claude settings.json could not be updated; it was not changed.",
                _settingsPath);
        }
        catch (Exception)
        {
            return new ClaudeStatusLineConfigurationResult(
                ClaudeStatusLineConfigurationState.UnexpectedFailure,
                "Claude statusLine setup failed safely; settings were not changed.",
                _settingsPath);
        }
    }

    private JsonObject ReadSettings()
    {
        if (!File.Exists(_settingsPath))
        {
            return new JsonObject();
        }

        var node = JsonNode.Parse(File.ReadAllText(_settingsPath));
        return node as JsonObject
            ?? throw new JsonException("Claude settings root must be a JSON object.");
    }

    private void WriteSettingsAtomically(JsonObject root)
    {
        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new IOException("Claude settings directory is unavailable.");
        Directory.CreateDirectory(directory);

        if (File.Exists(_settingsPath))
        {
            File.Copy(_settingsPath, GetBackupPath(), overwrite: true);
        }

        var temporaryPath = Path.Combine(directory, $".settings.{Guid.NewGuid():N}.tmp");
        try
        {
            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(temporaryPath, json + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool IsWidgetManaged(JsonNode statusLine) =>
        statusLine is JsonObject existing
        && TryGetString(existing["command"])?.Contains(
            BridgeExecutableName,
            StringComparison.OrdinalIgnoreCase) == true;

    private static string? TryGetString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;

    private string GetBackupPath() =>
        Path.Combine(
            Path.GetDirectoryName(_settingsPath)!,
            "settings.llm-limits-widget.backup.json");

    private static string CreateCommand(string bridgeExecutablePath)
    {
        var escapedPath = bridgeExecutablePath.Replace("'", "''", StringComparison.Ordinal);
        return $"powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"& '{escapedPath}'\"";
    }
}

public enum ClaudeStatusLineConfigurationState
{
    Configured,
    AlreadyConfigured,
    ExistingUserStatusLine,
    BridgeUnavailable,
    InvalidSettings,
    AccessDenied,
    IoFailed,
    UnexpectedFailure
}

public sealed record ClaudeStatusLineConfigurationResult(
    ClaudeStatusLineConfigurationState State,
    string Detail,
    string SettingsPath)
{
    public bool IsReady => State is ClaudeStatusLineConfigurationState.Configured
        or ClaudeStatusLineConfigurationState.AlreadyConfigured;
}
