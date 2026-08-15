using System.IO;

namespace LLMLimitsWidget.FloatingOverlay;

internal static class LocalExecutableLocator
{
    public static string ResolveCodex()
    {
        return Resolve(
            "CODEX_CLI_PATH",
            "codex.exe",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "OpenAI",
                "Codex",
                "bin",
                "codex.exe"));
    }

    public static string ResolveClaude()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CODE_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packageRoot = Path.Combine(
            localAppData,
            "Packages",
            "Claude_pzs8sxrjxfjjc",
            "LocalCache",
            "Roaming",
            "Claude",
            "claude-code");
        var installed = Directory.Exists(packageRoot)
            ? Directory.EnumerateFiles(packageRoot, "claude.exe", SearchOption.AllDirectories)
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()
            : null;

        return installed ?? "claude.exe";
    }

    public static string Resolve(
        string environmentVariable,
        string pathFallback,
        params string[] candidatePaths)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        foreach (var candidate in candidatePaths)
        {
            if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return pathFallback;
    }
}
