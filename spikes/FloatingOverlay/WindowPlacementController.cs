using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace LLMLimitsWidget.FloatingOverlay;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

internal readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(1, Right - Left);
    public int Height => Math.Max(1, Bottom - Top);

    public static PixelRect FromNative(NativeRect value) =>
        new(value.Left, value.Top, value.Right, value.Bottom);

    public NativeRect ToNative() => new()
    {
        Left = Left,
        Top = Top,
        Right = Right,
        Bottom = Bottom
    };
}

internal static class WindowPlacementGeometry
{
    public static PixelRect Restore(
        PixelRect monitorBounds,
        int windowWidth,
        int windowHeight,
        double relativeX,
        double relativeY,
        PlacementAnchor anchors)
    {
        var width = Math.Max(1, windowWidth);
        var height = Math.Max(1, windowHeight);
        var availableX = Math.Max(0, monitorBounds.Width - width);
        var availableY = Math.Max(0, monitorBounds.Height - height);
        var left = anchors.HasFlag(PlacementAnchor.Left)
            ? monitorBounds.Left
            : anchors.HasFlag(PlacementAnchor.Right)
                ? monitorBounds.Right - width
                : monitorBounds.Left + (int)Math.Round(availableX * Math.Clamp(relativeX, 0, 1));
        var top = anchors.HasFlag(PlacementAnchor.Top)
            ? monitorBounds.Top
            : anchors.HasFlag(PlacementAnchor.Bottom)
                ? monitorBounds.Bottom - height
                : monitorBounds.Top + (int)Math.Round(availableY * Math.Clamp(relativeY, 0, 1));

        return ClampAndSnap(
            new PixelRect(left, top, left + width, top + height),
            monitorBounds,
            0);
    }

    public static PixelRect ClampAndSnap(PixelRect candidate, PixelRect monitorBounds, int snapDistance)
    {
        return ClampAndSnap(candidate, monitorBounds, snapDistance, EdgeConstraints.All);
    }

    public static PixelRect ClampAndSnap(
        PixelRect candidate,
        PixelRect monitorBounds,
        int snapDistance,
        EdgeConstraints constraints)
    {
        var width = candidate.Width;
        var height = candidate.Height;
        var availableWidth = Math.Max(0, monitorBounds.Right - monitorBounds.Left);
        var availableHeight = Math.Max(0, monitorBounds.Bottom - monitorBounds.Top);

        var left = candidate.Left;
        var top = candidate.Top;

        if (constraints.Left && constraints.Right && width >= availableWidth)
        {
            left = monitorBounds.Left;
        }
        else
        {
            var maximumLeft = monitorBounds.Right - width;
            if (constraints.Left && Math.Abs(left - monitorBounds.Left) <= snapDistance)
            {
                left = monitorBounds.Left;
            }
            else if (constraints.Right && Math.Abs(candidate.Right - monitorBounds.Right) <= snapDistance)
            {
                left = maximumLeft;
            }

            if (constraints.Left)
            {
                left = Math.Max(left, monitorBounds.Left);
            }

            if (constraints.Right)
            {
                left = Math.Min(left, maximumLeft);
            }
        }

        if (constraints.Top && constraints.Bottom && height >= availableHeight)
        {
            top = monitorBounds.Top;
        }
        else
        {
            var maximumTop = monitorBounds.Bottom - height;
            if (constraints.Top && Math.Abs(top - monitorBounds.Top) <= snapDistance)
            {
                top = monitorBounds.Top;
            }
            else if (constraints.Bottom && Math.Abs(candidate.Bottom - monitorBounds.Bottom) <= snapDistance)
            {
                top = maximumTop;
            }

            if (constraints.Top)
            {
                top = Math.Max(top, monitorBounds.Top);
            }

            if (constraints.Bottom)
            {
                top = Math.Min(top, maximumTop);
            }
        }

        return new PixelRect(left, top, left + width, top + height);
    }
}

internal readonly record struct EdgeConstraints(bool Left, bool Top, bool Right, bool Bottom)
{
    public static EdgeConstraints All => new(true, true, true, true);
}

internal interface IWindowZOrderController
{
    bool EnsureTopmostOnce();
    bool SetTopmostBand(bool topmost);
}

internal sealed class WindowPlacementController : IDisposable, IWindowZOrderController
{
    private const int WmMoving = 0x0216;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private const int WmWindowPosChanging = 0x0046;
    private const int WmDisplayChange = 0x007E;
    private const int WmSettingChange = 0x001A;
    private const int WmDpiChanged = 0x02E0;
    private const int WmPowerBroadcast = 0x0218;
    private const int PbtApmResumeAutomatic = 0x0012;
    private const uint MonitorDefaultToNearest = 2;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const double SnapDistanceDip = 14;

    private readonly System.Windows.Window _window;
    private HwndSource? _source;
    private IntPtr _handle;
    private bool _disposed;
    private bool _inMoveLoop;
    private bool _applyingPlacement;
    private IntPtr _lastDragMonitor;
    private WindowPlacementSettings? _pendingRestore;
    private readonly System.Windows.Threading.DispatcherTimer _topologyTimer;

    public WindowPlacementController(System.Windows.Window window)
    {
        _window = window;
        _topologyTimer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(200),
            System.Windows.Threading.DispatcherPriority.Background,
            TopologyTimer_Tick,
            window.Dispatcher);
        _topologyTimer.Stop();
    }

    public event EventHandler? PlacementCommitted;

    public bool EnsureTopmostOnce()
    {
        return SetTopmostBand(true);
    }

    public bool SetTopmostBand(bool topmost)
    {
        if (_disposed || !_window.IsVisible)
        {
            return false;
        }

        EnsureAttached();
        return _handle != IntPtr.Zero
            && SetWindowPos(
                _handle,
                topmost ? HwndTopmost : new IntPtr(-2),
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    public void Attach()
    {
        if (_source is not null)
        {
            return;
        }

        _handle = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WindowProc);
    }

    public void Restore(WindowPlacementSettings? placement)
    {
        EnsureAttached();
        if (_handle == IntPtr.Zero || !GetWindowRect(_handle, out var nativeWindowRect))
        {
            return;
        }

        var monitor = FindSavedMonitor(placement) ?? GetCursorMonitor();
        if (!TryGetMonitor(monitor, out var monitorInfo))
        {
            return;
        }

        var currentMonitor = MonitorFromRect(ref nativeWindowRect, MonitorDefaultToNearest);
        var currentDpi = GetDpiForWindow(_handle);
        var needsDpiTransition = placement?.IsValid == true
            && (currentMonitor != monitor || (placement.SavedDpi > 0 && currentDpi != placement.SavedDpi));
        if (needsDpiTransition)
        {
            _pendingRestore = placement;
            var current = PixelRect.FromNative(nativeWindowRect);
            var monitorBounds = PixelRect.FromNative(monitorInfo.Monitor);
            var staging = WindowPlacementGeometry.ClampAndSnap(
                new PixelRect(
                    monitorBounds.Left,
                    monitorBounds.Top,
                    monitorBounds.Left + current.Width,
                    monitorBounds.Top + current.Height),
                monitorBounds,
                0);
            MoveWindow(staging);
            _window.Dispatcher.BeginInvoke(
                CompletePendingRestore,
                System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }

        ApplyPlacement(monitor, placement);
    }

    private void ApplyPlacement(IntPtr monitor, WindowPlacementSettings? placement)
    {
        if (_handle == IntPtr.Zero
            || !GetWindowRect(_handle, out var nativeWindowRect)
            || !TryGetMonitor(monitor, out var monitorInfo))
        {
            return;
        }

        var windowRect = PixelRect.FromNative(nativeWindowRect);
        var monitorBounds = PixelRect.FromNative(monitorInfo.Monitor);
        var relativeX = placement?.IsValid == true ? Math.Clamp(placement.RelativeX, 0, 1) : 1;
        var relativeY = placement?.IsValid == true ? Math.Clamp(placement.RelativeY, 0, 1) : 1;
        var anchors = placement?.Anchors ?? PlacementAnchor.None;
        var restored = WindowPlacementGeometry.Restore(
            monitorBounds,
            windowRect.Width,
            windowRect.Height,
            relativeX,
            relativeY,
            anchors);
        MoveWindow(restored);
    }

    private void CompletePendingRestore()
    {
        var placement = _pendingRestore;
        _pendingRestore = null;
        if (placement?.IsValid != true || _disposed)
        {
            return;
        }

        var monitor = FindSavedMonitor(placement) ?? GetCursorMonitor();
        ApplyPlacement(monitor, placement);
        QueuePlacementCommitted();
    }

    public void PlaceAtDefault()
    {
        EnsureAttached();
        if (_handle == IntPtr.Zero || !GetWindowRect(_handle, out var nativeWindowRect))
        {
            return;
        }

        var monitor = GetCursorMonitor();
        if (!TryGetMonitor(monitor, out var monitorInfo))
        {
            return;
        }

        var windowRect = PixelRect.FromNative(nativeWindowRect);
        var monitorBounds = PixelRect.FromNative(monitorInfo.Monitor);
        var margin = Math.Max(4, GetSnapDistancePixels() / 3);
        var target = new PixelRect(
            monitorBounds.Right - windowRect.Width - margin,
            monitorBounds.Bottom - windowRect.Height - margin,
            monitorBounds.Right - margin,
            monitorBounds.Bottom - margin);
        MoveWindow(WindowPlacementGeometry.ClampAndSnap(target, monitorBounds, 0));
    }

    public void NormalizeCurrentWindow(bool snapToEdges, IntPtr monitorOverride = default)
    {
        EnsureAttached();
        if (_handle == IntPtr.Zero || !GetWindowRect(_handle, out var nativeRect))
        {
            return;
        }

        var rect = PixelRect.FromNative(nativeRect);
        var monitor = monitorOverride != IntPtr.Zero
            ? monitorOverride
            : MonitorFromRect(ref nativeRect, MonitorDefaultToNearest);
        if (!TryGetMonitor(monitor, out var monitorInfo))
        {
            return;
        }

        var normalized = WindowPlacementGeometry.ClampAndSnap(
            rect,
            PixelRect.FromNative(monitorInfo.Monitor),
            snapToEdges ? GetSnapDistancePixels() : 0);
        MoveWindow(normalized);
    }

    public WindowPlacementSettings? Capture()
    {
        EnsureAttached();
        if (_handle == IntPtr.Zero || !GetWindowRect(_handle, out var nativeRect))
        {
            return null;
        }

        var monitor = MonitorFromRect(ref nativeRect, MonitorDefaultToNearest);
        if (!TryGetMonitor(monitor, out var monitorInfo))
        {
            return null;
        }

        var rect = PixelRect.FromNative(nativeRect);
        var monitorBounds = PixelRect.FromNative(monitorInfo.Monitor);
        var availableX = Math.Max(0, monitorBounds.Width - rect.Width);
        var availableY = Math.Max(0, monitorBounds.Height - rect.Height);
        var identity = GetMonitorIdentity(monitorInfo.DeviceName);
        var edgeTolerance = Math.Max(2, GetSnapDistancePixels() / 3);
        var anchors = PlacementAnchor.None;
        if (Math.Abs(rect.Left - monitorBounds.Left) <= edgeTolerance)
        {
            anchors |= PlacementAnchor.Left;
        }
        else if (Math.Abs(rect.Right - monitorBounds.Right) <= edgeTolerance)
        {
            anchors |= PlacementAnchor.Right;
        }

        if (Math.Abs(rect.Top - monitorBounds.Top) <= edgeTolerance)
        {
            anchors |= PlacementAnchor.Top;
        }
        else if (Math.Abs(rect.Bottom - monitorBounds.Bottom) <= edgeTolerance)
        {
            anchors |= PlacementAnchor.Bottom;
        }

        return new WindowPlacementSettings
        {
            IsValid = true,
            MonitorDeviceName = monitorInfo.DeviceName,
            MonitorDeviceId = identity.DeviceId,
            MonitorDeviceKey = identity.DeviceKey,
            RelativeX = availableX == 0 ? 0 : Math.Clamp((double)(rect.Left - monitorBounds.Left) / availableX, 0, 1),
            RelativeY = availableY == 0 ? 0 : Math.Clamp((double)(rect.Top - monitorBounds.Top) / availableY, 0, 1),
            Anchors = anchors,
            SavedDpi = _handle == IntPtr.Zero ? 96u : Math.Max(96u, GetDpiForWindow(_handle)),
            MonitorLeft = monitorInfo.Monitor.Left,
            MonitorTop = monitorInfo.Monitor.Top,
            MonitorWidth = monitorInfo.Monitor.Right - monitorInfo.Monitor.Left,
            MonitorHeight = monitorInfo.Monitor.Bottom - monitorInfo.Monitor.Top
        };
    }

    public PixelRect? GetCurrentWindowRect()
    {
        EnsureAttached();
        return _handle != IntPtr.Zero && GetWindowRect(_handle, out var rect)
            ? PixelRect.FromNative(rect)
            : null;
    }

    public uint GetCurrentDpi()
    {
        EnsureAttached();
        var dpi = _handle == IntPtr.Zero ? 0 : GetDpiForWindow(_handle);
        return dpi == 0 ? 96u : dpi;
    }

    public void AnchorResize(PixelRect startRect, bool anchorRight, bool anchorBottom)
    {
        EnsureAttached();
        if (_handle == IntPtr.Zero || !GetWindowRect(_handle, out var currentNative))
        {
            return;
        }

        var current = PixelRect.FromNative(currentNative);
        var left = anchorRight ? startRect.Right - current.Width : startRect.Left;
        var top = anchorBottom ? startRect.Bottom - current.Height : startRect.Top;
        var candidate = new PixelRect(left, top, left + current.Width, top + current.Height);
        var nativeCandidate = candidate.ToNative();
        var monitor = MonitorFromRect(ref nativeCandidate, MonitorDefaultToNearest);
        if (!TryGetMonitor(monitor, out var monitorInfo))
        {
            return;
        }

        MoveWindow(WindowPlacementGeometry.ClampAndSnap(
            candidate,
            PixelRect.FromNative(monitorInfo.Monitor),
            0));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _topologyTimer.Stop();
        _source?.RemoveHook(WindowProc);
        _source = null;
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmEnterSizeMove)
        {
            _inMoveLoop = true;
            _lastDragMonitor = IntPtr.Zero;
        }
        else if (message == WmMoving && lParam != IntPtr.Zero)
        {
            var candidateNative = Marshal.PtrToStructure<NativeRect>(lParam);
            var monitor = GetCursorMonitor();
            if (TryGetMonitor(monitor, out var monitorInfo))
            {
                _lastDragMonitor = monitor;
                var adjusted = WindowPlacementGeometry.ClampAndSnap(
                    PixelRect.FromNative(candidateNative),
                    PixelRect.FromNative(monitorInfo.Monitor),
                    GetSnapDistancePixels(),
                    GetDragConstraints(monitorInfo));
                Marshal.StructureToPtr(adjusted.ToNative(), lParam, false);
                handled = true;
                return new IntPtr(1);
            }
        }
        else if (message == WmExitSizeMove)
        {
            _inMoveLoop = false;
            NormalizeCurrentWindow(snapToEdges: true, _lastDragMonitor);
            _lastDragMonitor = IntPtr.Zero;
            QueuePlacementCommitted();
        }
        else if (message == WmWindowPosChanging && lParam != IntPtr.Zero && !_applyingPlacement)
        {
            ConstrainWindowPosition(lParam);
        }
        else if (message is WmDisplayChange or WmSettingChange or WmDpiChanged
                 || (message == WmPowerBroadcast && wParam.ToInt32() == PbtApmResumeAutomatic))
        {
            _topologyTimer.Stop();
            _topologyTimer.Start();
        }

        return IntPtr.Zero;
    }

    private void ConstrainWindowPosition(IntPtr windowPositionPointer)
    {
        var position = Marshal.PtrToStructure<WindowPosition>(windowPositionPointer);
        var noMove = (position.Flags & SwpNoMove) != 0;
        var noSize = (position.Flags & SwpNoSize) != 0;
        if (noMove && noSize)
        {
            return;
        }

        if (!GetWindowRect(_handle, out var currentNative))
        {
            return;
        }

        var current = PixelRect.FromNative(currentNative);
        var width = noSize ? current.Width : Math.Max(1, position.Width);
        var height = noSize ? current.Height : Math.Max(1, position.Height);
        var left = noMove ? current.Left : position.X;
        var top = noMove ? current.Top : position.Y;
        var candidate = new PixelRect(left, top, left + width, top + height);
        var candidateNative = candidate.ToNative();
        var monitor = _inMoveLoop
            ? GetCursorMonitor()
            : MonitorFromRect(ref candidateNative, MonitorDefaultToNearest);
        if (!TryGetMonitor(monitor, out var monitorInfo))
        {
            return;
        }

        var adjusted = WindowPlacementGeometry.ClampAndSnap(
            candidate,
            PixelRect.FromNative(monitorInfo.Monitor),
            _inMoveLoop ? GetSnapDistancePixels() : 0,
            _inMoveLoop ? GetDragConstraints(monitorInfo) : EdgeConstraints.All);
        position.X = adjusted.Left;
        position.Y = adjusted.Top;
        if (noMove && (position.X != current.Left || position.Y != current.Top))
        {
            position.Flags &= ~SwpNoMove;
        }
        Marshal.StructureToPtr(position, windowPositionPointer, false);
    }

    private EdgeConstraints GetDragConstraints(MonitorInfo target)
    {
        var left = true;
        var top = true;
        var right = true;
        var bottom = true;

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            if (!TryGetMonitor(monitor, out var other)
                || string.Equals(other.DeviceName, target.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var verticalOverlap = Math.Min(target.Monitor.Bottom, other.Monitor.Bottom)
                - Math.Max(target.Monitor.Top, other.Monitor.Top);
            var horizontalOverlap = Math.Min(target.Monitor.Right, other.Monitor.Right)
                - Math.Max(target.Monitor.Left, other.Monitor.Left);

            if (verticalOverlap > 0 && other.Monitor.Right == target.Monitor.Left)
            {
                left = false;
            }
            if (verticalOverlap > 0 && other.Monitor.Left == target.Monitor.Right)
            {
                right = false;
            }
            if (horizontalOverlap > 0 && other.Monitor.Bottom == target.Monitor.Top)
            {
                top = false;
            }
            if (horizontalOverlap > 0 && other.Monitor.Top == target.Monitor.Bottom)
            {
                bottom = false;
            }

            return true;
        }, IntPtr.Zero);

        return new EdgeConstraints(left, top, right, bottom);
    }

    private void TopologyTimer_Tick(object? sender, EventArgs e)
    {
        _topologyTimer.Stop();
        if (_disposed)
        {
            return;
        }

        if (_pendingRestore is not null)
        {
            CompletePendingRestore();
            return;
        }

        NormalizeCurrentWindow(snapToEdges: false);
        QueuePlacementCommitted();
    }

    private void QueuePlacementCommitted()
    {
        _window.Dispatcher.BeginInvoke(
            () => PlacementCommitted?.Invoke(this, EventArgs.Empty),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void EnsureAttached()
    {
        if (_source is null)
        {
            Attach();
        }
    }

    private int GetSnapDistancePixels()
    {
        var dpi = _handle == IntPtr.Zero ? 96u : GetDpiForWindow(_handle);
        if (dpi == 0)
        {
            dpi = 96;
        }

        return Math.Max(1, (int)Math.Round(SnapDistanceDip * dpi / 96d));
    }

    private IntPtr GetCursorMonitor()
    {
        return GetCursorPos(out var cursor)
            ? MonitorFromPoint(cursor, MonitorDefaultToNearest)
            : MonitorFromWindow(_handle, MonitorDefaultToNearest);
    }

    private IntPtr? FindSavedMonitor(WindowPlacementSettings? placement)
    {
        if (placement?.IsValid != true)
        {
            return null;
        }

        IntPtr keyExact = IntPtr.Zero;
        IntPtr idExact = IntPtr.Zero;
        IntPtr nameExact = IntPtr.Zero;
        IntPtr nearest = IntPtr.Zero;
        var nearestDistance = double.MaxValue;
        var savedCenterX = placement.MonitorLeft + (placement.MonitorWidth / 2d);
        var savedCenterY = placement.MonitorTop + (placement.MonitorHeight / 2d);

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            if (!TryGetMonitor(monitor, out var info))
            {
                return true;
            }

            var identity = GetMonitorIdentity(info.DeviceName);
            if (!string.IsNullOrWhiteSpace(placement.MonitorDeviceKey)
                && string.Equals(identity.DeviceKey, placement.MonitorDeviceKey, StringComparison.OrdinalIgnoreCase))
            {
                keyExact = monitor;
                return false;
            }

            if (idExact == IntPtr.Zero
                && !string.IsNullOrWhiteSpace(placement.MonitorDeviceId)
                && string.Equals(identity.DeviceId, placement.MonitorDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                idExact = monitor;
            }

            if (nameExact == IntPtr.Zero
                && string.Equals(info.DeviceName, placement.MonitorDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                nameExact = monitor;
            }

            var centerX = info.Monitor.Left + ((info.Monitor.Right - info.Monitor.Left) / 2d);
            var centerY = info.Monitor.Top + ((info.Monitor.Bottom - info.Monitor.Top) / 2d);
            var distance = Math.Pow(centerX - savedCenterX, 2) + Math.Pow(centerY - savedCenterY, 2);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = monitor;
            }

            return true;
        }, IntPtr.Zero);

        if (keyExact != IntPtr.Zero)
        {
            return keyExact;
        }
        if (idExact != IntPtr.Zero)
        {
            return idExact;
        }
        if (nameExact != IntPtr.Zero)
        {
            return nameExact;
        }
        return nearest != IntPtr.Zero ? nearest : null;
    }

    private static MonitorIdentity GetMonitorIdentity(string displayName)
    {
        var device = new DisplayDevice
        {
            Size = Marshal.SizeOf<DisplayDevice>(),
            DeviceName = string.Empty,
            DeviceString = string.Empty,
            DeviceId = string.Empty,
            DeviceKey = string.Empty
        };

        return EnumDisplayDevices(displayName, 0, ref device, 0)
            ? new MonitorIdentity(device.DeviceId, device.DeviceKey)
            : new MonitorIdentity(null, null);
    }

    private static bool TryGetMonitor(IntPtr monitor, out MonitorInfo monitorInfo)
    {
        monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>(),
            DeviceName = string.Empty
        };
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref monitorInfo);
    }

    private void MoveWindow(PixelRect rect)
    {
        _applyingPlacement = true;
        try
        {
            SetWindowPos(
                _handle,
                IntPtr.Zero,
                rect.Left,
                rect.Top,
                rect.Width,
                rect.Height,
                SwpNoActivate | SwpNoZOrder);
        }
        finally
        {
            _applyingPlacement = false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public IntPtr Window;
        public IntPtr InsertAfter;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public uint Flags;
    }

    private readonly record struct MonitorIdentity(string? DeviceId, string? DeviceKey);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public uint StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clipRect, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string deviceName, uint deviceNumber, ref DisplayDevice displayDevice, uint flags);
}
