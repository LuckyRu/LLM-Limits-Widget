using LLMLimitsWidget.FloatingOverlay;

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

Console.WriteLine($"Placement geometry: {cases.Length + 4} cases passed.");
return 0;
