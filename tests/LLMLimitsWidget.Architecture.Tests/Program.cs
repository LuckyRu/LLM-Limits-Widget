using System.Reflection;
using LLMLimitsWidget.Domain;

var domainAssembly = typeof(AppState).Assembly;
var forbidden = domainAssembly
    .GetReferencedAssemblies()
    .Select(reference => reference.Name)
    .Where(name => name is not null)
    .Where(name => name!.Contains("Presentation", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Wpf", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Windows", StringComparison.OrdinalIgnoreCase)
        || name.Contains("FloatingOverlay", StringComparison.OrdinalIgnoreCase))
    .ToArray();

if (forbidden.Length > 0)
{
    Console.Error.WriteLine($"Domain has forbidden references: {string.Join(", ", forbidden)}");
    return 1;
}

var domainSourceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "LLMLimitsWidget.Domain"));
var forbiddenTokens = new[]
{
    "System.Windows",
    "System.Diagnostics.Process",
    "FileSystemWatcher",
    "DateTimeOffset.UtcNow",
    "DateTimeOffset.Now",
    "Random.Shared",
    "WidgetLogger"
};
foreach (var sourceFile in Directory.EnumerateFiles(domainSourceRoot, "*.cs", SearchOption.TopDirectoryOnly))
{
    var source = File.ReadAllText(sourceFile);
    foreach (var token in forbiddenTokens)
    {
        if (source.Contains(token, StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"Domain source contains forbidden token '{token}': {sourceFile}");
            return 1;
        }
    }
}

Console.WriteLine("Architecture M1: domain boundary passed.");
return 0;
