using LLMLimitsWidget.Domain;
using LLMLimitsWidget.Provider.Codex;

var failures = new List<string>();
var captured = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
var fixture = """
{
  "id": 2,
  "result": {
    "rateLimits": {
      "limitId": "codex",
      "primary": { "usedPercent": 35.25, "windowDurationMins": 10080, "resetsAt": 1787200270 },
      "secondary": null
    }
  }
}
""";

var parsed = CodexRateLimitsParser.Parse(
    fixture,
    captured,
    captured.AddMilliseconds(10),
    generation: 4,
    sequence: 9,
    EffectId.New());
Assert(parsed.IsSuccess, "C-001 parses the current Codex bucket shape");
AssertEqual(64.75m, parsed.Observation!.Windows[LimitPeriod.SevenDays].Remaining.Value, "C-002 preserves decimal remaining percent");
AssertEqual(4L, parsed.Observation.Generation, "C-005 carries generation");
AssertEqual(9L, parsed.Observation.Sequence, "C-005 carries sequence");

var malformed = CodexRateLimitsParser.Parse("{", captured, captured, 0, 1, EffectId.New());
AssertEqual(ErrorCode.MalformedPayload, malformed.Error!.Code, "C-004 maps malformed JSON to typed error");

var unsupported = CodexRateLimitsParser.Parse(
    "{\"result\":{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":1,\"windowDurationMins\":15,\"resetsAt\":1787200270}}}}",
    captured,
    captured,
    0,
    1,
    EffectId.New());
AssertEqual(ErrorCode.NoSupportedWindows, unsupported.Error!.Code, "C-003 rejects unsupported windows");

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

Console.WriteLine("Codex provider M3: all cases passed.");
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
