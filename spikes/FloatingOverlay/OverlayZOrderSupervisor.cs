using System.Runtime.InteropServices;
using System.Threading;

namespace LLMLimitsWidget.FloatingOverlay;

internal sealed class OverlayZOrderSupervisor : IDisposable
{
    internal static readonly TimeSpan[] BurstIntervals =
    [
        TimeSpan.FromMilliseconds(75),
        TimeSpan.FromMilliseconds(75),
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromMilliseconds(300),
        TimeSpan.FromMilliseconds(600)
    ];

    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectReorder = 0x8004;
    private const int ObjidWindow = -4;
    private const int ChildIdSelf = 0;
    private const uint WineventOutOfContext = 0;
    private const uint WineventSkipOwnProcess = 0x0002;
    private const uint GaRoot = 2;

    private readonly IWindowZOrderController _placementController;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private readonly System.Windows.Threading.DispatcherTimer _watchdog;
    private readonly System.Windows.Threading.DispatcherTimer _burstTimer;
    private readonly WinEventDelegate _winEventCallback;
    private IntPtr _foregroundHook;
    private IntPtr _showHook;
    private IntPtr _reorderHook;
    private volatile bool _disposed;
    private volatile bool _visible;
    private volatile bool _menuOpen;
    private int _burstStep;
    private int _burstExtensionsRemaining;
    private bool _burstExtensionRequested;
    private int _eventDispatchPending;
    private bool _topmostHealthy = true;

    public OverlayZOrderSupervisor(
        IWindowZOrderController placementController,
        System.Windows.Threading.Dispatcher dispatcher)
    {
        _placementController = placementController;
        _dispatcher = dispatcher;
        _winEventCallback = WinEventCallback;
        _watchdog = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromSeconds(1),
            System.Windows.Threading.DispatcherPriority.Background,
            WatchdogTimer_Tick,
            dispatcher);
        _burstTimer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(75),
            System.Windows.Threading.DispatcherPriority.Input,
            BurstTimer_Tick,
            dispatcher);
        _watchdog.Stop();
        _burstTimer.Stop();
    }

    public bool HooksAvailable => _foregroundHook != IntPtr.Zero
        && _showHook != IntPtr.Zero
        && _reorderHook != IntPtr.Zero;

    public event EventHandler<bool>? TopmostHealthChanged;

    public bool Attach()
    {
        if (_disposed)
        {
            return false;
        }

        if (HooksAvailable)
        {
            return true;
        }

        ReleaseHooks();
        var foregroundHook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            IntPtr.Zero,
            _winEventCallback,
            0,
            0,
            WineventOutOfContext | WineventSkipOwnProcess);
        var showHook = SetWinEventHook(
            EventObjectShow,
            EventObjectShow,
            IntPtr.Zero,
            _winEventCallback,
            0,
            0,
            WineventOutOfContext | WineventSkipOwnProcess);
        var reorderHook = SetWinEventHook(
            EventObjectReorder,
            EventObjectReorder,
            IntPtr.Zero,
            _winEventCallback,
            0,
            0,
            WineventOutOfContext | WineventSkipOwnProcess);

        if (foregroundHook == IntPtr.Zero || showHook == IntPtr.Zero || reorderHook == IntPtr.Zero)
        {
            if (foregroundHook != IntPtr.Zero)
            {
                UnhookWinEvent(foregroundHook);
            }

            if (showHook != IntPtr.Zero)
            {
                UnhookWinEvent(showHook);
            }

            if (reorderHook != IntPtr.Zero)
            {
                UnhookWinEvent(reorderHook);
            }

            return false;
        }

        _foregroundHook = foregroundHook;
        _showHook = showHook;
        _reorderHook = reorderHook;
        return true;
    }

    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (!visible)
        {
            StopPendingReasserts();
            _watchdog.Stop();
            return;
        }

        _watchdog.Start();
        Reassert("visible");
    }

    public bool SetMenuOpen(bool open)
    {
        _menuOpen = open;
        if (open)
        {
            StopPendingReasserts();
            return _placementController.SetTopmostBand(false);
        }

        if (_visible)
        {
            return Reassert("management-menu-closed");
        }

        return true;
    }

    public bool Reassert(string reason)
    {
        if (_disposed || !_visible || _menuOpen)
        {
            return false;
        }

        if (_burstTimer.IsEnabled)
        {
            if (!string.Equals(reason, "watchdog", StringComparison.Ordinal))
            {
                _burstExtensionRequested = true;
            }
            return _topmostHealthy;
        }

        if (!EnsureTopmostCore())
        {
            return false;
        }

        _burstStep = 0;
        _burstExtensionsRemaining = 1;
        _burstExtensionRequested = false;
        _burstTimer.Interval = BurstIntervals[0];
        _burstTimer.Start();
        return true;
    }

    public bool EnsureTopmostNow()
    {
        return !_disposed && _visible && EnsureTopmostCore();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _visible = false;
        _watchdog.Stop();
        StopPendingReasserts();
        ReleaseHooks();
    }

    private void WatchdogTimer_Tick(object? sender, EventArgs e)
    {
        Reassert("watchdog");
    }

    private void BurstTimer_Tick(object? sender, EventArgs e)
    {
        if (_disposed || !_visible || _menuOpen)
        {
            _burstTimer.Stop();
            return;
        }

        EnsureTopmostCore();
        _burstStep++;
        if (_burstStep >= BurstIntervals.Length)
        {
            if (_burstExtensionRequested && _burstExtensionsRemaining > 0)
            {
                _burstExtensionRequested = false;
                _burstExtensionsRemaining--;
                _burstStep = 0;
                _burstTimer.Interval = BurstIntervals[0];
                return;
            }

            _burstTimer.Stop();
            return;
        }

        _burstTimer.Interval = BurstIntervals[_burstStep];
    }

    private void StopPendingReasserts()
    {
        _burstTimer.Stop();
        _burstStep = 0;
        _burstExtensionsRemaining = 0;
        _burstExtensionRequested = false;
        Interlocked.Exchange(ref _eventDispatchPending, 0);
    }

    private bool EnsureTopmostCore()
    {
        var healthy = _placementController.EnsureTopmostOnce();
        if (_topmostHealthy != healthy)
        {
            _topmostHealthy = healthy;
            TopmostHealthChanged?.Invoke(this, healthy);
        }

        return healthy;
    }

    private void WinEventCallback(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime)
    {
        if (_disposed || !_visible || _menuOpen || hwnd == IntPtr.Zero)
        {
            return;
        }

        if (eventType is EventObjectShow or EventObjectReorder
            && (idObject != ObjidWindow
                || idChild != ChildIdSelf
                || GetAncestor(hwnd, GaRoot) != hwnd))
        {
            return;
        }

        if (Interlocked.Exchange(ref _eventDispatchPending, 1) != 0
            || _dispatcher.HasShutdownStarted)
        {
            return;
        }

        _dispatcher.BeginInvoke(
            () =>
            {
                Interlocked.Exchange(ref _eventDispatchPending, 0);
                var reason = eventType switch
                {
                    EventSystemForeground => "foreground",
                    EventObjectShow => "top-level-show",
                    _ => "top-level-reorder"
                };
                Reassert(reason);
            },
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ReleaseHooks()
    {
        if (_foregroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }

        if (_showHook != IntPtr.Zero)
        {
            UnhookWinEvent(_showHook);
            _showHook = IntPtr.Zero;
        }

        if (_reorderHook != IntPtr.Zero)
        {
            UnhookWinEvent(_reorderHook);
            _reorderHook = IntPtr.Zero;
        }
    }

    private delegate void WinEventDelegate(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}
