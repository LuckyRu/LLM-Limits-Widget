using LLMLimitsWidget.FloatingOverlay;
using System.IO;
using System.Text.Json;

var failures = new List<string>();
var runRealSmoke = args.Any(argument => string.Equals(argument, "--real", StringComparison.OrdinalIgnoreCase));

var demoCoordinator = new LimitsCoordinator(
    new ILimitsDataSource[]
    {
        new DemoCodexLimitsDataSource(),
        new DemoClaudeLimitsDataSource()
    });
var demoSnapshot = await demoCoordinator.RefreshAsync();
AssertEqual(2, demoSnapshot.Providers.Count, "both demo providers publish a snapshot");
AssertEqual(1, demoSnapshot.Providers[LimitProviderId.Codex].Windows.Count, "Codex has one weekly window");
AssertEqual(2, demoSnapshot.Providers[LimitProviderId.Claude].Windows.Count, "Claude has two windows");
AssertEqual(62d, demoSnapshot.Providers[LimitProviderId.Codex].Windows[0].SafeRemainingPercent!.Value, "Codex percentage is preserved");
await demoCoordinator.DisposeAsync();

var codexFixture = """
{
  "id": 2,
  "result": {
    "rateLimits": {
      "limitId": "codex",
      "primary": { "usedPercent": 35, "windowDurationMins": 10080, "resetsAt": 1787200270 },
      "secondary": null
    },
    "rateLimitsByLimitId": {
      "codex": {
        "limitId": "codex",
        "primary": { "usedPercent": 35, "windowDurationMins": 10080, "resetsAt": 1787200270 },
        "secondary": null
      }
    }
  }
}
""";
var parsedCodex = CodexRateLimitsParser.Parse(codexFixture, DateTimeOffset.UtcNow);
AssertEqual(LimitDataStatus.Fresh, parsedCodex.Status, "Codex app-server fixture is fresh");
AssertEqual(1, parsedCodex.Windows.Count, "Codex parser ignores absent secondary window");
AssertEqual(65d, parsedCodex.Windows[0].SafeRemainingPercent!.Value, "Codex parser converts used to remaining");
AssertEqual(LimitWindowKind.Weekly, parsedCodex.Windows[0].Kind, "Codex parser recognizes weekly duration");

var claudeResult = """
Current session: 27% used · resets Aug 15, 7:59am (Europe/Bucharest)
Current week (all models): 47% used · resets Aug 17, 4:59pm (Europe/Bucharest)
""";
var claudeFixture = JsonSerializer.Serialize(new { is_error = false, result = claudeResult });
var parsedClaude = ClaudeUsageParser.Parse(
    claudeFixture,
    new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.FromHours(3)));
AssertEqual(LimitDataStatus.Fresh, parsedClaude.Status, "Claude usage fixture is fresh");
AssertEqual(2, parsedClaude.Windows.Count, "Claude parser reads both supported windows");
AssertEqual(73d, parsedClaude.Windows[0].SafeRemainingPercent!.Value, "Claude parser converts session usage");
AssertEqual(53d, parsedClaude.Windows[1].SafeRemainingPercent!.Value, "Claude parser converts weekly usage");
AssertEqual(true, parsedClaude.Windows.All(window => window.ResetAt.HasValue), "Claude parser reads reset timestamps");

var countdownNow = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.FromHours(3));
AssertEqual(
    "in 3h 40m",
    CountdownFormatter.Format(countdownNow.AddHours(3).AddMinutes(40), countdownNow),
    "countdown keeps hours and minutes near the cutoff");
AssertEqual(
    "in 42m",
    CountdownFormatter.Format(countdownNow.AddMinutes(42), countdownNow),
    "countdown compresses to minutes below one hour");
AssertEqual(
    "in 8m",
    CountdownFormatter.Format(countdownNow.AddMinutes(8), countdownNow),
    "countdown stays compact when the cutoff is imminent");
AssertEqual(
    "in 2d 4h",
    CountdownFormatter.Format(countdownNow.AddDays(2).AddHours(4), countdownNow),
    "countdown uses days and hours for distant resets");
AssertEqual(
    CountdownUrgency.Normal,
    CountdownFormatter.GetUrgency(countdownNow.AddHours(3), countdownNow),
    "countdown urgency is normal outside the near window");
AssertEqual(
    CountdownUrgency.Near,
    CountdownFormatter.GetUrgency(countdownNow.AddMinutes(30), countdownNow),
    "countdown urgency changes within one hour");
AssertEqual(
    CountdownUrgency.Critical,
    CountdownFormatter.GetUrgency(countdownNow.AddMinutes(8), countdownNow),
    "countdown urgency changes within ten minutes");

var statusLineFixture = """
{
  "version": "2.1.227",
  "rate_limits": {
    "five_hour": { "used_percentage": 23.5, "resets_at": 1786793399 },
    "seven_day": { "used_percentage": 41.2, "resets_at": 1787057999 }
  }
}
""";
var parsedStatusLine = ClaudeStatusLineParser.Parse(statusLineFixture, DateTimeOffset.UtcNow);
AssertEqual(2, parsedStatusLine.Windows.Count, "Claude statusLine parser reads both windows");
AssertEqual(76.5d, parsedStatusLine.Windows[0].SafeRemainingPercent!.Value, "statusLine converts session usage");
AssertEqual(58.8d, parsedStatusLine.Windows[1].SafeRemainingPercent!.Value, "statusLine converts weekly usage");
var bridgeSnapshotPath = Path.Combine(
    Path.GetTempPath(),
    $"llm-limits-domain-test-{Guid.NewGuid():N}.json");
try
{
    AssertEqual(
        0,
        await ClaudeStatusLineBridge.RunAsync(new StringReader(statusLineFixture), bridgeSnapshotPath),
        "statusLine bridge accepts valid input");
    var statusLineSource = new ClaudeStatusLineLimitsDataSource(
        bridgeSnapshotPath,
        TimeSpan.FromMinutes(3));
    var statusLineSnapshot = await statusLineSource.GetSnapshotAsync(CancellationToken.None);
    AssertEqual(LimitDataStatus.Fresh, statusLineSnapshot.Status, "statusLine snapshot is fresh");
    AssertEqual(2, statusLineSnapshot.Windows.Count, "statusLine snapshot is readable by the provider");
}
finally
{
    File.Delete(bridgeSnapshotPath);
}

var hybridDirectSnapshot = new ProviderLimitsSnapshot(
    LimitProviderId.Claude,
    DateTimeOffset.Now,
    new[]
    {
        new LimitWindowSnapshot(LimitWindowKind.FiveHour, "5h", 81, DateTimeOffset.Now.AddHours(1)),
        new LimitWindowSnapshot(LimitWindowKind.SevenDay, "7d", 50, DateTimeOffset.Now.AddDays(1))
    });
var hybridDirect = new FixedForceSource(hybridDirectSnapshot);
var hybrid = new ClaudeHybridLimitsDataSource(
    new ClaudeStatusLineLimitsDataSource(
        Path.Combine(Path.GetTempPath(), $"missing-statusline-{Guid.NewGuid():N}.json")),
    hybridDirect);
var hybridFirst = await hybrid.GetSnapshotAsync(CancellationToken.None);
var hybridSecond = await hybrid.GetSnapshotAsync(CancellationToken.None);
AssertEqual(LimitDataStatus.Fresh, hybridFirst.Status, "hybrid uses direct snapshot when statusLine is absent");
AssertEqual(LimitDataStatus.Stale, hybridSecond.Status, "hybrid keeps last direct snapshot during cooldown");
AssertEqual(2, hybridSecond.Windows.Count, "hybrid stale fallback keeps all Claude windows");
AssertEqual(1, hybridDirect.ReadCount, "hybrid cooldown prevents repeated direct calls");

var updates = 0;
var healthySource = new FixedSource(
    LimitProviderId.Codex,
    new ProviderLimitsSnapshot(
        LimitProviderId.Codex,
        DateTimeOffset.UtcNow,
        new[]
        {
            new LimitWindowSnapshot(LimitWindowKind.Weekly, "W", 140, null)
        }));
var failingSource = new FixedSource(
    LimitProviderId.Claude,
    new ProviderLimitsSnapshot(
        LimitProviderId.Claude,
        DateTimeOffset.UtcNow,
        new[]
        {
            new LimitWindowSnapshot(LimitWindowKind.FiveHour, "5h", 20, null)
        }),
    failAfterFirstRead: true);
var failureIsolatedCoordinator = new LimitsCoordinator(new[] { healthySource, failingSource });
failureIsolatedCoordinator.SnapshotChanged += _ => updates++;
var firstSnapshot = await failureIsolatedCoordinator.RefreshAsync();
var secondSnapshot = await failureIsolatedCoordinator.RefreshAsync();
AssertEqual(2, updates, "each completed refresh publishes one snapshot");
AssertEqual(100d, firstSnapshot.Providers[LimitProviderId.Codex].Windows[0].SafeRemainingPercent!.Value, "percentages are capped at 100");
AssertEqual(LimitDataStatus.Fresh, firstSnapshot.Providers[LimitProviderId.Claude].Status, "successful provider is fresh");
AssertEqual(LimitDataStatus.Stale, secondSnapshot.Providers[LimitProviderId.Claude].Status, "provider failure keeps stale data");
AssertEqual(100d, secondSnapshot.Providers[LimitProviderId.Codex].Windows[0].SafeRemainingPercent!.Value, "healthy provider still updates after peer failure");
await failureIsolatedCoordinator.DisposeAsync();

var stateDirectory = Path.Combine(Path.GetTempPath(), $"llm-limits-state-test-{Guid.NewGuid():N}");
try
{
    var persistedSnapshot = new ProviderLimitsSnapshot(
        LimitProviderId.Codex,
        DateTimeOffset.UtcNow,
        new[] { new LimitWindowSnapshot(LimitWindowKind.Weekly, "W", 67.5, DateTimeOffset.UtcNow.AddDays(2)) });
    await using (var persistingSupervisor = new ProviderSupervisor(
                     new FixedSource(LimitProviderId.Codex, persistedSnapshot),
                     new ProviderStateStore(LimitProviderId.Codex, stateDirectory),
                     new ProviderRefreshPolicy(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30))))
    {
        var saved = await persistingSupervisor.ForceRefreshAsync(CancellationToken.None);
        AssertEqual(LimitDataStatus.Fresh, saved.Status, "supervisor persists a valid successful snapshot");
    }

    await using (var restoredSupervisor = new ProviderSupervisor(
                     new ThrowingSource(LimitProviderId.Codex, "temporary network failure"),
                     new ProviderStateStore(LimitProviderId.Codex, stateDirectory),
                     new ProviderRefreshPolicy(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30))))
    {
        var restored = await restoredSupervisor.GetSnapshotAsync(CancellationToken.None);
        AssertEqual(LimitDataStatus.Stale, restored.Status, "supervisor keeps persisted data after a failed refresh");
        AssertEqual(67.5d, restored.Windows[0].SafeRemainingPercent!.Value, "persisted value survives a restart");
    }
}
finally
{
    if (Directory.Exists(stateDirectory))
    {
        Directory.Delete(stateDirectory, recursive: true);
    }
}

var backoffStateDirectory = Path.Combine(Path.GetTempPath(), $"llm-limits-backoff-{Guid.NewGuid():N}");
try
{
    await using var backoffSupervisor = new ProviderSupervisor(
        new ThrowingSource(LimitProviderId.Claude, "login required"),
        new ProviderStateStore(LimitProviderId.Claude, backoffStateDirectory),
        new ProviderRefreshPolicy(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30)));
    var first = await backoffSupervisor.GetSnapshotAsync(CancellationToken.None);
    var second = await backoffSupervisor.GetSnapshotAsync(CancellationToken.None);
    AssertEqual(LimitDataStatus.ActionRequired, first.Status, "authentication failure is actionable instead of fabricated data");
    AssertEqual(LimitDataStatus.ActionRequired, second.Status, "backoff preserves the actionable state");
}
finally
{
    if (Directory.Exists(backoffStateDirectory))
    {
        Directory.Delete(backoffStateDirectory, recursive: true);
    }
}

var singleFlightStateDirectory = Path.Combine(Path.GetTempPath(), $"llm-limits-single-flight-{Guid.NewGuid():N}");
try
{
    await using var serializedSupervisor = new ProviderSupervisor(
        new SlowSource(LimitProviderId.Codex),
        new ProviderStateStore(LimitProviderId.Codex, singleFlightStateDirectory),
        new ProviderRefreshPolicy(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30)));
    await Task.WhenAll(
        serializedSupervisor.ForceRefreshAsync(CancellationToken.None),
        serializedSupervisor.ForceRefreshAsync(CancellationToken.None));
    AssertEqual(1, SlowSource.MaxConcurrentReads, "supervisor serializes concurrent forced refreshes");
}
finally
{
    if (Directory.Exists(singleFlightStateDirectory))
    {
        Directory.Delete(singleFlightStateDirectory, recursive: true);
    }
}

if (runRealSmoke)
{
    await RunRealProviderSmokeAsync();
}

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

Console.WriteLine("Limits domain: all cases passed.");
return 0;

static async Task RunRealProviderSmokeAsync()
{
    var sources = new ILimitsDataSource[]
    {
        new CodexAppServerLimitsDataSource(),
        new ClaudeHybridLimitsDataSource()
    };
    await using var coordinator = new LimitsCoordinator(sources);
    var snapshot = await coordinator.RefreshAsync();
    foreach (var provider in Enum.GetValues<LimitProviderId>())
    {
        if (!snapshot.TryGetProvider(provider, out var providerSnapshot))
        {
            Console.WriteLine($"REAL {provider}: missing");
            continue;
        }

        var windows = string.Join(
            ", ",
            providerSnapshot.Windows.Select(window =>
                $"{window.Label}={window.SafeRemainingPercent:0.##}% reset={window.ResetAt?.ToLocalTime():yyyy-MM-dd HH:mm}"));
        Console.WriteLine($"REAL {provider}: {providerSnapshot.Status}; {windows}");
    }
}

void AssertEqual<T>(T expected, T actual, string name)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        failures.Add($"{name}: expected {expected}, got {actual}");
    }
}

sealed class FixedSource(
    LimitProviderId provider,
    ProviderLimitsSnapshot snapshot,
    bool failAfterFirstRead = false) : ILimitsDataSource
{
    private int _reads;

    public LimitProviderId Provider => provider;

    public Task<ProviderLimitsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (failAfterFirstRead && Interlocked.Increment(ref _reads) > 1)
        {
            throw new InvalidOperationException("simulated provider failure");
        }

        return Task.FromResult(snapshot);
    }
}

sealed class FixedForceSource(ProviderLimitsSnapshot snapshot) : IForceRefreshableLimitsDataSource
{
    public int ReadCount { get; private set; }

    public LimitProviderId Provider => snapshot.Provider;

    public Task<ProviderLimitsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        return Task.FromResult(snapshot);
    }

    public Task<ProviderLimitsSnapshot> ForceRefreshAsync(CancellationToken cancellationToken)
    {
        return GetSnapshotAsync(cancellationToken);
    }
}

sealed class ThrowingSource(LimitProviderId provider, string message) : ILimitsDataSource
{
    public LimitProviderId Provider => provider;

    public Task<ProviderLimitsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        Task.FromException<ProviderLimitsSnapshot>(new InvalidOperationException(message));
}

sealed class SlowSource(LimitProviderId provider) : ILimitsDataSource
{
    private static int _activeReads;
    public static int MaxConcurrentReads;

    public LimitProviderId Provider => provider;

    public async Task<ProviderLimitsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var concurrent = Interlocked.Increment(ref _activeReads);
        InterlockedExtensions.Max(ref MaxConcurrentReads, concurrent);
        try
        {
            await Task.Delay(75, cancellationToken);
            return new ProviderLimitsSnapshot(
                Provider,
                DateTimeOffset.UtcNow,
                new[] { new LimitWindowSnapshot(LimitWindowKind.Weekly, "W", 50, DateTimeOffset.UtcNow.AddDays(1)) });
        }
        finally
        {
            Interlocked.Decrement(ref _activeReads);
        }
    }
}

static class InterlockedExtensions
{
    public static void Max(ref int location, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref location);
            if (current >= value || Interlocked.CompareExchange(ref location, value, current) == current)
            {
                return;
            }
        }
    }
}
