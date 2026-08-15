using System.Collections.Immutable;
using LLMLimitsWidget.Domain;
using LLMLimitsWidget.Presentation;

var failures = new List<string>();
var now = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
var remaining = RemainingPercent.Create(62.125m, ProviderId.Codex, TransportId.CodexAppServer, now).Value!;
var window = new LimitWindow(
    LimitPeriod.SevenDays,
    remaining,
    now.AddHours(3).AddMinutes(40),
    new ObservationCursor(0, 1, now, "1"),
    new DataProvenance(TransportId.CodexAppServer, now, "1"));
var codex = ProviderState.Initial(ProviderId.Codex) with
{
    LastKnownGood = new ProviderLimits(
        ProviderId.Codex,
        ObservationId.New(),
        now,
        ImmutableDictionary<LimitPeriod, LimitWindow>.Empty.Add(LimitPeriod.SevenDays, window)),
    Freshness = DataFreshness.Fresh,
    AggregateHealth = ProviderHealth.Healthy
};
var state = AppState.Empty with
{
    Providers = AppState.Empty.Providers.SetItem(ProviderId.Codex, codex)
};

var widget = new WidgetViewModel();
var changed = 0;
widget.Codex.SevenDays.PropertyChanged += (_, _) => changed++;
widget.Apply(state, now);
AssertEqual("62.13%", widget.Codex.SevenDays.PercentText, "V-001 preserves compact decimal percent");
AssertEqual("in 3 hr 40 min", widget.Codex.SevenDays.CountdownText, "V-002 formats compact countdown");
Assert(widget.Codex.SevenDays.IsVisible, "V-003 exposes available window");
var firstChangeCount = changed;
widget.Apply(state, now.AddSeconds(10));
Assert(changed > firstChangeCount, "V-004 countdown updates when rendered text changes");
var secondChangeCount = changed;
widget.Apply(state, now.AddSeconds(1));
AssertEqual(secondChangeCount, changed, "V-004 avoids redundant property notifications");

AssertEqual("in 45 sec", CountdownTextFormatter.Format(now.AddSeconds(45), now), "V-005 formats seconds");
AssertEqual("in 2 d 4 hr", CountdownTextFormatter.Format(now.AddDays(2).AddHours(4), now), "V-005 formats days");
AssertEqual(
    now.AddSeconds(30),
    CountdownTextFormatter.GetNextVisualChangeAt(now.AddHours(3).AddMinutes(39).AddSeconds(30), now),
    "V-006 schedules next minute boundary");
AssertEqual(
    now.AddMinutes(30),
    CountdownTextFormatter.GetNextVisualChangeAt(now.AddDays(2).AddHours(4).AddMinutes(30), now),
    "V-007 schedules multi-day countdown at next hour boundary");
AssertEqual(
    now.AddHours(4),
    CountdownTextFormatter.GetNextVisualChangeAt(now.AddDays(13).AddHours(4), now),
    "V-007 schedules weekly countdown at next day boundary");
AssertEqual(
    null,
    new WidgetViewModel().GetNextVisualChangeAt(now),
    "V-008 does not schedule a timer without data");
AssertEqual(
    null,
    CountdownTextFormatter.GetNextVisualChangeAt(now, now),
    "V-008 stops countdown after reset time");

var diagnostics = new DiagnosticsViewModel();
diagnostics.Apply(state, now);
Assert(
    diagnostics.Codex.Windows.Contains("62.13%", StringComparison.Ordinal),
    "V-009 diagnostics projects normalized limit values");
Assert(
    diagnostics.Codex.Transports.Contains("Codex app-server", StringComparison.Ordinal),
    "V-009 diagnostics exposes transport health without raw payload");

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

Console.WriteLine("Presentation M9: all cases passed.");
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
