using System.Collections.Immutable;
using LLMLimitsWidget.Application;
using LLMLimitsWidget.Domain;
using LLMLimitsWidget.Infrastructure.Windows;

var failures = new List<string>();
var now = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
var runner = new ScriptedRunner(new HiddenProcessResult(
    0,
    "{\"is_error\":false,\"result\":\"Current session: 27.25% used · resets Aug 15, 7:59am (UTC)\\nCurrent week (all models): 47.5% used · resets Aug 17, 4:59pm (UTC)\"}",
    string.Empty));
var transport = new ClaudeDirectCliTransport("claude.exe", runner, new FixedTimeProvider(now));
var context = new AttemptContext(
    ProviderId.Claude,
    TransportId.ClaudeDirectCli,
    AttemptId.New(),
    EffectId.New(),
    0,
    1,
    new RefreshReasonSet(RefreshReason.Startup),
    now.AddSeconds(30));

var success = await transport.AcquireAsync(context, CancellationToken.None);
Assert(success is AttemptSucceeded, "I-001 direct adapter maps valid CLI JSON to success");
var observation = ((AttemptSucceeded)success).Observation;
AssertEqual(72.75m, observation.Windows[LimitPeriod.FiveHours].Remaining.Value, "I-001 preserves 5h precision");
AssertEqual(52.5m, observation.Windows[LimitPeriod.SevenDays].Remaining.Value, "I-001 preserves weekly precision");
AssertEqual("claude.exe", runner.LastRequest!.FileName, "I-002 uses configured executable");
Assert(runner.LastRequest.Arguments.Contains("/usage"), "I-002 sends /usage");

var missing = new ClaudeDirectCliTransport(
    "missing.exe",
    new ThrowingRunner(new HiddenProcessException(ProcessFailureKind.ExecutableNotFound, "missing")),
    new FixedTimeProvider(now));
var missingOutcome = await missing.AcquireAsync(context, CancellationToken.None);
AssertEqual(
    ErrorCode.ExecutableNotFound,
    ((AttemptFailed)missingOutcome).Error.Code,
    "I-003 maps missing executable to typed error");

var codexSession = new FakeCodexSession("""
{"result":{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":12.5,"windowDurationMins":10080,"resetsAt":1786793399}}}}}
""");
var codexTransport = new CodexAppServerTransport(codexSession, new FixedTimeProvider(now));
var codexOutcome = await codexTransport.AcquireAsync(
    context with
    {
        Provider = ProviderId.Codex,
        Transport = TransportId.CodexAppServer
    },
    CancellationToken.None);
Assert(codexOutcome is AttemptSucceeded, "I-003 Codex adapter maps app-server JSON to success");
AssertEqual(
    87.5m,
    ((AttemptSucceeded)codexOutcome).Observation.Windows[LimitPeriod.SevenDays].Remaining.Value,
    "I-003 Codex preserves decimal precision");

var snapshotPath = Path.Combine(Path.GetTempPath(), $"llm-limits-widget-{Guid.NewGuid():N}.json");
await File.WriteAllTextAsync(
    snapshotPath,
    "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":10.5,\"resets_at\":1786793399}}}");
try
{
    var reader = new ClaudeStatusLineFileReader(snapshotPath, new FixedTimeProvider(now));
    var snapshot = await reader.ReadAsync(0, 2, EffectId.New(), CancellationToken.None);
    Assert(snapshot.IsSuccess, "I-004 statusLine file reader parses snapshot");
    Assert(
        !string.IsNullOrWhiteSpace(snapshot.Observation!.SourceRevision),
        "I-004 statusLine carries file source revision");
}
finally
{
    File.Delete(snapshotPath);
}

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

Console.WriteLine("Infrastructure M7: all cases passed.");
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

sealed class ScriptedRunner(HiddenProcessResult result) : IHiddenProcessRunner
{
    public HiddenProcessRequest? LastRequest { get; private set; }

    public Task<HiddenProcessResult> RunAsync(HiddenProcessRequest request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(result);
    }
}

sealed class ThrowingRunner(Exception exception) : IHiddenProcessRunner
{
    public Task<HiddenProcessResult> RunAsync(HiddenProcessRequest request, CancellationToken cancellationToken) =>
        Task.FromException<HiddenProcessResult>(exception);
}

sealed class FakeCodexSession(string response) : ICodexAppServerSession
{
    public Task<string> ReadRateLimitsAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.FromResult(response);
}

sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
