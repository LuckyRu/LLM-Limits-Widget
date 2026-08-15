using System.Text.Json;
using LLMLimitsWidget.Domain;
using LLMLimitsWidget.Provider.Claude;

var failures = new List<string>();
var now = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.FromHours(3));
var statusLine = """
{
  "rate_limits": {
    "five_hour": { "used_percentage": 23.5, "resets_at": 1786793399 },
    "seven_day": { "used_percentage": 41.2, "resets_at": 1787057999 }
  }
}
""";

var parsedStatusLine = ClaudeStatusLineParser.Parse(statusLine, now, now.AddSeconds(1), 2, 4, EffectId.New());
Assert(parsedStatusLine.IsSuccess, "L-001 parses statusLine rate limits");
AssertEqual(76.5m, parsedStatusLine.Observation!.Windows[LimitPeriod.FiveHours].Remaining.Value, "L-001 preserves 5h precision");
AssertEqual(58.8m, parsedStatusLine.Observation.Windows[LimitPeriod.SevenDays].Remaining.Value, "L-001 preserves weekly precision");
AssertEqual(TransportId.ClaudeStatusLine, parsedStatusLine.Observation.Transport, "L-003 keeps source provenance");

var usageText = """
Current session: 27.25% used · resets Aug 15, 7:59am (Europe/Bucharest)
Current week (all models): 47.5% used · resets Aug 17, 4:59pm (Europe/Bucharest)
""";
var usage = ClaudeUsageParser.Parse(
    JsonSerializer.Serialize(new { is_error = false, result = usageText }),
    now,
    now.AddSeconds(1),
    3,
    5,
    EffectId.New());
Assert(usage.IsSuccess, "L-002 parses direct /usage result");
AssertEqual(72.75m, usage.Observation!.Windows[LimitPeriod.FiveHours].Remaining.Value, "L-002 preserves direct 5h precision");
AssertEqual(52.5m, usage.Observation.Windows[LimitPeriod.SevenDays].Remaining.Value, "L-002 preserves direct weekly precision");
AssertEqual(TransportId.ClaudeDirectCli, usage.Observation.Transport, "L-002 keeps direct provenance");

var malformed = ClaudeStatusLineParser.Parse("{", now, now, 0, 1, EffectId.New());
AssertEqual(ErrorCode.MalformedPayload, malformed.Error!.Code, "L-007 maps malformed statusLine JSON");

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

Console.WriteLine("Claude provider M4: all cases passed.");
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
