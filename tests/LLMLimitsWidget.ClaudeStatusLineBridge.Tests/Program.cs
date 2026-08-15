using LLMLimitsWidget.ClaudeStatusLineBridge;

var failures = new List<string>();
var snapshotPath = Path.Combine(Path.GetTempPath(), $"llm-limits-bridge-{Guid.NewGuid():N}.json");
var previousPath = Environment.GetEnvironmentVariable("LLM_LIMITS_CLAUDE_SNAPSHOT");
try
{
    Environment.SetEnvironmentVariable("LLM_LIMITS_CLAUDE_SNAPSHOT", snapshotPath);
    var result = await StatusLineBridge.RunAsync(
        new StringReader("""
        {"cwd":"C:/private","rate_limits":{"five_hour":{"used_percentage":23.5,"resets_at":1786793399},"seven_day":{"used_percentage":41.2,"resets_at":1787000000}}}
        """),
        TextWriter.Null);
    AssertEqual(0, result, "B-001 bridge exits successfully");
    var snapshot = await File.ReadAllTextAsync(snapshotPath);
    Assert(snapshot.Contains("\"five_hour\"", StringComparison.Ordinal), "B-001 retains supported rate limits");
    Assert(!snapshot.Contains("cwd", StringComparison.OrdinalIgnoreCase), "B-001 excludes private session metadata");

    File.Delete(snapshotPath);
    result = await StatusLineBridge.RunAsync(
        new StringReader("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":-1,\"resets_at\":1}}}"),
        TextWriter.Null);
    AssertEqual(0, result, "B-002 malformed input never breaks Claude statusLine");
    Assert(!File.Exists(snapshotPath), "B-002 malformed input creates no plausible snapshot");
}
finally
{
    Environment.SetEnvironmentVariable("LLM_LIMITS_CLAUDE_SNAPSHOT", previousPath);
    if (File.Exists(snapshotPath))
    {
        File.Delete(snapshotPath);
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

Console.WriteLine("Claude statusLine bridge: all cases passed.");
return 0;

void Assert(bool condition, string name)
{
    if (!condition)
    {
        failures.Add($"{name}: condition was false");
    }
}

void AssertEqual<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        failures.Add($"{name}: expected {expected}, got {actual}");
    }
}
