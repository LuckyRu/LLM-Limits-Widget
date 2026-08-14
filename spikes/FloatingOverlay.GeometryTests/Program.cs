using LLMLimitsWidget.FloatingOverlay;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

var cases = new (string Name, PixelRect Candidate, PixelRect MonitorBounds, int Snap, PixelRect Expected)[]
{
    ("inside remains unchanged", new(100, 100, 300, 180), new(0, 0, 1920, 1040), 14, new(100, 100, 300, 180)),
    ("left overflow clamps", new(-500, 100, -300, 180), new(0, 0, 1920, 1040), 14, new(0, 100, 200, 180)),
    ("right overflow clamps", new(1900, 100, 2100, 180), new(0, 0, 1920, 1040), 14, new(1720, 100, 1920, 180)),
    ("top overflow clamps", new(100, -200, 300, -120), new(0, 0, 1920, 1040), 14, new(100, 0, 300, 80)),
    ("bottom overflow clamps", new(100, 1020, 300, 1100), new(0, 0, 1920, 1040), 14, new(100, 960, 300, 1040)),
    ("left snap", new(10, 100, 210, 180), new(0, 0, 1920, 1040), 14, new(0, 100, 200, 180)),
    ("right snap", new(1709, 100, 1909, 180), new(0, 0, 1920, 1040), 14, new(1720, 100, 1920, 180)),
    ("top snap with offset bounds", new(100, 52, 300, 132), new(0, 40, 1920, 1080), 14, new(100, 40, 300, 120)),
    ("bottom snap with inset bounds", new(100, 949, 300, 1029), new(0, 0, 1920, 1040), 14, new(100, 960, 300, 1040)),
    ("taskbar overlap remains inside physical monitor", new(100, 1000, 300, 1080), new(0, 0, 1920, 1080), 14, new(100, 1000, 300, 1080)),
    ("negative monitor coordinates", new(-2700, 100, -2500, 180), new(-2560, 0, 0, 1400), 14, new(-2560, 100, -2360, 180)),
    ("offset left bound", new(0, 100, 200, 180), new(48, 0, 1920, 1040), 14, new(48, 100, 248, 180)),
    ("window wider than monitor keeps top-left handle", new(-900, 100, 1200, 180), new(0, 0, 1920, 1040), 14, new(0, 100, 2100, 180)),
    ("window taller than monitor keeps top-left handle", new(100, -900, 300, 1200), new(0, 0, 1920, 1040), 14, new(100, 0, 300, 2100)),
    ("corner overflow clamps both axes", new(2000, 1200, 2200, 1280), new(0, 0, 1920, 1040), 14, new(1720, 960, 1920, 1040))
};

var failures = new List<string>();
foreach (var testCase in cases)
{
    var actual = WindowPlacementGeometry.ClampAndSnap(
        testCase.Candidate,
        testCase.MonitorBounds,
        testCase.Snap);
    if (actual != testCase.Expected)
    {
        failures.Add($"{testCase.Name}: expected {testCase.Expected}, got {actual}");
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

var passageCandidate = new PixelRect(1800, 100, 2000, 180);
var passageResult = WindowPlacementGeometry.ClampAndSnap(
    passageCandidate,
    new PixelRect(0, 0, 1920, 1040),
    14,
    new EdgeConstraints(Left: true, Top: true, Right: false, Bottom: true));
if (passageResult != passageCandidate)
{
    failures.Add($"shared right edge must remain passable: expected {passageCandidate}, got {passageResult}");
}

var externalEdgeResult = WindowPlacementGeometry.ClampAndSnap(
    new PixelRect(-100, 100, 100, 180),
    new PixelRect(0, 0, 1920, 1040),
    14,
    new EdgeConstraints(Left: true, Top: true, Right: false, Bottom: true));
if (externalEdgeResult.Left != 0)
{
    failures.Add($"external left edge must remain guarded: got {externalEdgeResult}");
}

var anchoredRestore = WindowPlacementGeometry.Restore(
    new PixelRect(0, 40, 2560, 1400),
    500,
    200,
    0.2,
    0.3,
    PlacementAnchor.Right | PlacementAnchor.Bottom);
if (anchoredRestore != new PixelRect(2060, 1200, 2560, 1400))
{
    failures.Add($"right-bottom anchors must survive size and DPI changes: got {anchoredRestore}");
}

var relativeRestore = WindowPlacementGeometry.Restore(
    new PixelRect(-1920, 0, 0, 1040),
    320,
    80,
    0.5,
    0.25,
    PlacementAnchor.None);
if (relativeRestore != new PixelRect(-1120, 240, -800, 320))
{
    failures.Add($"relative placement must survive negative monitor coordinates: got {relativeRestore}");
}

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

AssertEqual(
    GhostStylePolicy.RequiredMask,
    GhostStylePolicy.AddedByController(GhostStylePolicy.Layered),
    "ghost policy adds both owned styles");
AssertEqual(
    GhostStylePolicy.NoActivate,
    GhostStylePolicy.AddedByController(GhostStylePolicy.Layered | GhostStylePolicy.Transparent),
    "ghost policy preserves pre-existing transparent style");
AssertEqual(
    0L,
    GhostStylePolicy.AddedByController(
        GhostStylePolicy.Layered | GhostStylePolicy.Transparent | GhostStylePolicy.NoActivate),
    "ghost policy owns no pre-existing styles");
const long unrelatedStyle = 0x00000400;
AssertEqual(
    GhostStylePolicy.Layered | unrelatedStyle,
    GhostStylePolicy.RemoveOwned(
        GhostStylePolicy.Layered | GhostStylePolicy.Transparent | unrelatedStyle,
        GhostStylePolicy.Transparent),
    "rollback removal preserves unrelated style bits");
AssertEqual(
    GhostStylePolicy.Layered | GhostStylePolicy.NoActivate | unrelatedStyle,
    GhostStylePolicy.RestoreOwned(
        GhostStylePolicy.Layered | unrelatedStyle,
        GhostStylePolicy.RequiredMask,
        GhostStylePolicy.NoActivate),
    "rollback restoration preserves concurrently added style bits");
var completeGhostState = GhostStylePolicy.EvaluateEffectiveState(
    GhostStylePolicy.Layered | GhostStylePolicy.RequiredMask,
    GhostStylePolicy.RequiredMask,
    previouslyEnabled: false);
AssertEqual(true, completeGhostState.IsFullyEnabled, "complete native ghost state is enabled");
var partialGhostState = GhostStylePolicy.EvaluateEffectiveState(
    GhostStylePolicy.Layered | GhostStylePolicy.Transparent,
    GhostStylePolicy.RequiredMask,
    previouslyEnabled: true);
AssertEqual(false, partialGhostState.IsFullyEnabled, "partial native ghost state is not enabled");
AssertEqual(true, partialGhostState.RequiresCleanup, "partial native ghost state requires cleanup");
AssertEqual(
    GhostModeTransitionResult.TopmostUnavailable,
    GhostTransitionPolicy.ResolveTopmostFailure(GhostModeTransitionResult.Success),
    "topmost failure with successful rollback is reported");
AssertEqual(
    GhostModeTransitionResult.RollbackFailed,
    GhostTransitionPolicy.ResolveTopmostFailure(GhostModeTransitionResult.StyleWriteFailed),
    "topmost failure with failed rollback is reported");
AssertEqual(
    "75,150,300",
    string.Join(",", OverlayZOrderSupervisor.BurstIntervals
        .Select((interval, index) => OverlayZOrderSupervisor.BurstIntervals
            .Take(index + 1)
            .Sum(item => item.TotalMilliseconds))),
    "topmost burst uses cumulative 75/150/300ms schedule");
AssertEqual(true, ManagementMenuZOrder.ShouldHideOverlayForRecovery(false, false), "menu fallback hides overlay");
AssertEqual(false, ManagementMenuZOrder.ShouldHideOverlayForRecovery(true, false), "menu demotion avoids hide fallback");
AssertEqual(false, ManagementMenuZOrder.ShouldHideOverlayForRecovery(false, true), "raised menu avoids hide fallback");

var migratedSettings = new WidgetSettings { SchemaVersion = 2, GhostModeEnabled = true };
migratedSettings.Normalize();
AssertEqual(WidgetSettings.CurrentSchemaVersion, migratedSettings.SchemaVersion, "known settings schema migrates");
AssertEqual(true, migratedSettings.CanPersist, "known settings schema remains writable");

var futureSettings = new WidgetSettings { SchemaVersion = WidgetSettings.CurrentSchemaVersion + 1 };
futureSettings.Normalize();
AssertEqual(WidgetSettings.CurrentSchemaVersion + 1, futureSettings.SchemaVersion, "future schema is not downgraded");
AssertEqual(false, futureSettings.CanPersist, "future schema is protected from overwrite");

AssertEqual(true, GhostStartupPolicy.ShouldRestore(true, false, true), "ghost restores with a recovery channel");
AssertEqual(false, GhostStartupPolicy.ShouldRestore(true, true, true), "--no-ghost suppresses startup restore");
AssertEqual(false, GhostStartupPolicy.ShouldRestore(true, false, false), "ghost does not restore without tray recovery");

RunNativeLifecycleChecks();
RunZOrderSupervisorChecks();

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

Console.WriteLine($"Placement, settings, and ghost lifecycle: {cases.Length + 48} cases passed.");
return 0;

void AssertEqual<T>(T expected, T actual, string name)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        failures.Add($"{name}: expected {expected}, got {actual}");
    }
}

void RunNativeLifecycleChecks()
{
    Exception? threadFailure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var root = new Grid { IsHitTestVisible = true, Focusable = true };
            var window = CreateTestWindow(root);
            window.Show();
            var handle = new WindowInteropHelper(window).Handle;
            var originalStyle = ReadExtendedStyle(handle);
            using var ghost = new GhostModeController(window, root);
            ghost.Attach();

            AssertEqual(GhostModeTransitionResult.Success, ghost.SetEnabled(true), "native ghost enable succeeds");
            var enabledStyle = ReadExtendedStyle(handle);
            AssertEqual(
                GhostStylePolicy.RequiredMask,
                enabledStyle & GhostStylePolicy.RequiredMask,
                "native ghost styles are applied");
            AssertEqual(false, root.IsHitTestVisible, "WPF hit testing is disabled");
            AssertEqual(false, root.Focusable, "WPF keyboard focus is disabled");
            AssertEqual(false, window.Focusable, "WPF window focus is disabled");

            AssertEqual(GhostModeTransitionResult.Success, ghost.SetEnabled(false), "native ghost disable succeeds");
            var disabledStyle = ReadExtendedStyle(handle);
            AssertEqual(
                originalStyle,
                disabledStyle,
                "native ghost preserves the complete extended style");
            AssertEqual(true, root.IsHitTestVisible, "WPF hit testing is restored");
            AssertEqual(true, root.Focusable, "WPF focusability is restored");
            AssertEqual(true, window.Focusable, "WPF window focusability is restored");

            AssertEqual(GhostModeTransitionResult.Success, ghost.SetEnabled(true), "native ghost re-enable succeeds");
            ghost.Dispose();
            AssertEqual(originalStyle, ReadExtendedStyle(handle), "dispose restores styles while HWND is alive");
            AssertEqual(true, root.IsHitTestVisible, "dispose restores WPF hit testing");
            AssertEqual(true, root.Focusable, "dispose restores root focusability");
            AssertEqual(true, window.Focusable, "dispose restores window focusability");

            using var placement = new WindowPlacementController(window);
            placement.Attach();
            window.Hide();
            AssertEqual(false, placement.EnsureTopmostOnce(), "hidden widget is not re-shown by topmost enforcement");
            AssertEqual(false, IsWindowVisible(handle), "hidden native window stays hidden");
            window.Close();
        }
        catch (Exception exception)
        {
            threadFailure = exception;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();

    if (threadFailure is not null)
    {
        failures.Add($"native lifecycle checks failed: {threadFailure}");
    }
}

void RunZOrderSupervisorChecks()
{
    var failingController = new FakeZOrderController(false, true);
    using var healthSupervisor = new OverlayZOrderSupervisor(
        failingController,
        System.Windows.Threading.Dispatcher.CurrentDispatcher);
    var healthEvents = new List<bool>();
    healthSupervisor.TopmostHealthChanged += (_, healthy) => healthEvents.Add(healthy);
    healthSupervisor.SetVisible(true);
    AssertEqual("False", string.Join(",", healthEvents), "runtime topmost failure emits unhealthy state");
    AssertEqual(true, healthSupervisor.EnsureTopmostNow(), "runtime topmost retry can recover");
    AssertEqual("False,True", string.Join(",", healthEvents), "runtime topmost recovery emits healthy state");
    healthSupervisor.SetVisible(false);

    var coalescingController = new FakeZOrderController(true);
    using var coalescingSupervisor = new OverlayZOrderSupervisor(
        coalescingController,
        System.Windows.Threading.Dispatcher.CurrentDispatcher);
    coalescingSupervisor.SetVisible(true);
    coalescingSupervisor.Reassert("storm-1");
    coalescingSupervisor.Reassert("storm-2");
    AssertEqual(1, coalescingController.TopmostCalls, "active topmost burst coalesces repeated signals");
    AssertEqual(true, coalescingSupervisor.SetMenuOpen(true), "menu mode demotes overlay");
    coalescingSupervisor.SetVisible(false);
}

static Window CreateTestWindow(UIElement content)
{
    return new Window
    {
        Width = 120,
        Height = 60,
        WindowStyle = WindowStyle.None,
        AllowsTransparency = true,
        Background = System.Windows.Media.Brushes.Transparent,
        ShowActivated = false,
        ShowInTaskbar = false,
        Content = content
    };
}

static long ReadExtendedStyle(IntPtr handle)
{
    SetLastError(0);
    var value = IntPtr.Size == 8
        ? GetWindowLongPtr(handle, -20)
        : new IntPtr(GetWindowLong(handle, -20));
    if (value == IntPtr.Zero && Marshal.GetLastWin32Error() != 0)
    {
        throw new InvalidOperationException($"GetWindowLong failed: {Marshal.GetLastWin32Error()}");
    }

    return value.ToInt64();
}

[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

[DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
static extern int GetWindowLong(IntPtr hwnd, int index);

[DllImport("user32.dll")]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool IsWindowVisible(IntPtr hwnd);

[DllImport("kernel32.dll", SetLastError = true)]
static extern void SetLastError(int errorCode);

internal sealed class FakeZOrderController(params bool[] topmostResults) : IWindowZOrderController
{
    private readonly Queue<bool> _topmostResults = new(topmostResults);

    public int TopmostCalls { get; private set; }

    public bool EnsureTopmostOnce()
    {
        TopmostCalls++;
        return _topmostResults.Count == 0 || _topmostResults.Dequeue();
    }

    public bool SetTopmostBand(bool topmost)
    {
        return !topmost || EnsureTopmostOnce();
    }
}
