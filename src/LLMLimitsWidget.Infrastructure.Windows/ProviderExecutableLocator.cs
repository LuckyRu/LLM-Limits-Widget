namespace LLMLimitsWidget.Infrastructure.Windows;

public static class ProviderExecutableLocator
{
    public static string ResolveCodex() =>
        ResolveEnvironmentOrPath("CODEX_CLI_PATH", "codex.exe");

    public static string ResolveClaude()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CODE_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var packageRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            "Claude_pzs8sxrjxfjjc",
            "LocalCache",
            "Roaming",
            "Claude",
            "claude-code");
        try
        {
            if (Directory.Exists(packageRoot))
            {
                var installed = Directory.EnumerateFiles(
                        packageRoot,
                        "claude.exe",
                        SearchOption.AllDirectories)
                    .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(installed))
                {
                    return installed;
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }

        return "claude.exe";
    }

    private static string ResolveEnvironmentOrPath(string variable, string fallback)
    {
        var configured = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(configured) ? fallback : configured;
    }
}
