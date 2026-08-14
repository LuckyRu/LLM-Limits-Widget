using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace LLMLimitsWidget.FloatingOverlay;

internal enum GhostModeTransitionResult
{
    Success,
    AlreadyInRequestedState,
    HandleUnavailable,
    UnsupportedWindowStyle,
    StyleReadFailed,
    StyleWriteFailed,
    StyleApplyFailed,
    VerificationFailed,
    TopmostUnavailable,
    ManagementMenuUnavailable,
    ForegroundRestoreUnavailable,
    ForegroundRestoreFailed,
    RollbackFailed
}

internal static class GhostStylePolicy
{
    public const long Layered = 0x00080000;
    public const long Transparent = 0x00000020;
    public const long NoActivate = 0x08000000;
    public const long RequiredMask = Transparent | NoActivate;

    public static long AddedByController(long currentStyle) => RequiredMask & ~currentStyle;
    public static long RemoveOwned(long currentStyle, long ownedMask) => currentStyle & ~ownedMask;
    public static long RestoreOwned(long currentStyle, long ownedMask, long ownedBefore) =>
        (currentStyle & ~ownedMask) | (ownedBefore & ownedMask);

    public static GhostEffectiveState EvaluateEffectiveState(
        long currentStyle,
        long ownedMask,
        bool previouslyEnabled)
    {
        var complete = (currentStyle & RequiredMask) == RequiredMask
            && (previouslyEnabled || ownedMask != 0);
        var ownedActive = (currentStyle & ownedMask) != 0;
        return new GhostEffectiveState(complete, !complete && ownedActive);
    }
}

internal readonly record struct GhostEffectiveState(bool IsFullyEnabled, bool RequiresCleanup);

internal sealed class GhostModeController : IDisposable
{
    private const int GwlExStyle = -20;
    private const int WmNchittest = 0x0084;
    private const int WmMouseactivate = 0x0021;
    private const int HtTransparent = -1;
    private const int MaNoactivate = 3;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    private readonly System.Windows.Window _window;
    private readonly UIElement _root;
    private readonly bool _originalIsHitTestVisible;
    private readonly bool _originalFocusable;
    private readonly bool _originalWindowFocusable;
    private HwndSource? _source;
    private IntPtr _handle;
    private long _addedMask;
    private bool _disposed;
    private bool _isEnabled;
    private bool _requiresCleanup;
    private bool _managementBypass;

    public GhostModeController(System.Windows.Window window, UIElement root)
    {
        _window = window;
        _root = root;
        _originalIsHitTestVisible = root.IsHitTestVisible;
        _originalFocusable = root.Focusable;
        _originalWindowFocusable = window.Focusable;
    }

    public bool IsEnabled => _isEnabled;
    public bool RequiresCleanup => _requiresCleanup;
    public bool IsInputSuppressed => _isEnabled || _requiresCleanup;

    public void SetManagementBypass(bool enabled)
    {
        if (_disposed)
        {
            return;
        }

        _managementBypass = enabled;
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

    public GhostModeTransitionResult SetEnabled(bool enabled, IntPtr foregroundToRestore = default)
    {
        if (_disposed)
        {
            return GhostModeTransitionResult.HandleUnavailable;
        }

        EnsureAttached();
        if (_handle == IntPtr.Zero)
        {
            return GhostModeTransitionResult.HandleUnavailable;
        }

        if (_isEnabled == enabled && !_requiresCleanup)
        {
            return GhostModeTransitionResult.AlreadyInRequestedState;
        }

        if (enabled && _requiresCleanup)
        {
            return GhostModeTransitionResult.RollbackFailed;
        }

        if (enabled
            && GetForegroundWindow() == _handle
            && !IsExternalWindow(foregroundToRestore))
        {
            return GhostModeTransitionResult.ForegroundRestoreUnavailable;
        }

        var result = enabled ? EnableNativePolicy() : DisableNativePolicy();
        if (result != GhostModeTransitionResult.Success)
        {
            SynchronizeEffectiveState();
            return result;
        }

        SetWpfInputPolicy(enabled);
        if (enabled && !TryRestoreExternalForeground(foregroundToRestore))
        {
            SetWpfInputPolicy(false);
            var rollbackResult = DisableNativePolicy();
            if (rollbackResult != GhostModeTransitionResult.Success)
            {
                SynchronizeEffectiveState();
            }
            return rollbackResult == GhostModeTransitionResult.Success
                ? GhostModeTransitionResult.ForegroundRestoreFailed
                : GhostModeTransitionResult.RollbackFailed;
        }

        _isEnabled = enabled;
        _requiresCleanup = false;
        return GhostModeTransitionResult.Success;
    }

    public GhostModeTransitionResult EnsureApplied()
    {
        if (_requiresCleanup)
        {
            return GhostModeTransitionResult.RollbackFailed;
        }

        if (!_isEnabled || _disposed)
        {
            return GhostModeTransitionResult.AlreadyInRequestedState;
        }

        EnsureAttached();
        var result = EnableNativePolicy();
        if (result == GhostModeTransitionResult.Success)
        {
            SetWpfInputPolicy(true);
        }
        else
        {
            SynchronizeEffectiveState();
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_isEnabled || _requiresCleanup)
        {
            var result = DisableNativePolicy();
            if (result == GhostModeTransitionResult.Success)
            {
                SetWpfInputPolicy(false);
                _isEnabled = false;
                _requiresCleanup = false;
            }
            else
            {
                SynchronizeEffectiveState();
            }
        }

        _managementBypass = false;
        _disposed = true;
        _source?.RemoveHook(WindowProc);
        _source = null;
    }

    private GhostModeTransitionResult EnableNativePolicy()
    {
        if (!TryReadExtendedStyle(out var current))
        {
            return GhostModeTransitionResult.StyleReadFailed;
        }

        if ((current & GhostStylePolicy.Layered) == 0)
        {
            return GhostModeTransitionResult.UnsupportedWindowStyle;
        }

        var addedThisAttempt = GhostStylePolicy.AddedByController(current);
        var next = current | GhostStylePolicy.RequiredMask;
        if (next != current)
        {
            if (!TryWriteExtendedStyle(next))
            {
                return GhostModeTransitionResult.StyleWriteFailed;
            }

            _addedMask |= addedThisAttempt;

            if (!ApplyFrameChanged())
            {
                return RollbackAddedStyles(addedThisAttempt)
                    ? GhostModeTransitionResult.StyleApplyFailed
                    : GhostModeTransitionResult.RollbackFailed;
            }
        }

        if (!TryReadExtendedStyle(out var verified))
        {
            return RollbackAddedStyles(addedThisAttempt)
                ? GhostModeTransitionResult.StyleReadFailed
                : GhostModeTransitionResult.RollbackFailed;
        }

        if ((verified & GhostStylePolicy.RequiredMask) != GhostStylePolicy.RequiredMask)
        {
            return RollbackAddedStyles(addedThisAttempt)
                ? GhostModeTransitionResult.VerificationFailed
                : GhostModeTransitionResult.RollbackFailed;
        }

        return GhostModeTransitionResult.Success;
    }

    private GhostModeTransitionResult DisableNativePolicy()
    {
        if (!TryReadExtendedStyle(out var current))
        {
            return GhostModeTransitionResult.StyleReadFailed;
        }

        var next = GhostStylePolicy.RemoveOwned(current, _addedMask);
        var ownedBefore = current & _addedMask;
        if (next != current)
        {
            if (!TryWriteExtendedStyle(next))
            {
                return GhostModeTransitionResult.StyleWriteFailed;
            }

            if (!ApplyFrameChanged())
            {
                return RestoreOwnedStyles(ownedBefore)
                    ? GhostModeTransitionResult.StyleApplyFailed
                    : GhostModeTransitionResult.RollbackFailed;
            }
        }

        if (!TryReadExtendedStyle(out var verified))
        {
            return RestoreOwnedStyles(ownedBefore)
                ? GhostModeTransitionResult.StyleReadFailed
                : GhostModeTransitionResult.RollbackFailed;
        }

        if ((verified & _addedMask) != 0)
        {
            return RestoreOwnedStyles(ownedBefore)
                ? GhostModeTransitionResult.VerificationFailed
                : GhostModeTransitionResult.RollbackFailed;
        }

        _addedMask = 0;
        return GhostModeTransitionResult.Success;
    }

    private bool RollbackAddedStyles(long addedThisAttempt)
    {
        if (addedThisAttempt == 0)
        {
            return true;
        }

        if (!TryReadExtendedStyle(out var latest))
        {
            return false;
        }

        var target = GhostStylePolicy.RemoveOwned(latest, addedThisAttempt);
        if (target != latest && (!TryWriteExtendedStyle(target) || !ApplyFrameChanged()))
        {
            return false;
        }

        var restored = TryReadExtendedStyle(out var verified) && (verified & addedThisAttempt) == 0;
        if (restored)
        {
            _addedMask &= ~addedThisAttempt;
        }

        return restored;
    }

    private bool RestoreOwnedStyles(long ownedBefore)
    {
        if (!TryReadExtendedStyle(out var latest))
        {
            return false;
        }

        var target = GhostStylePolicy.RestoreOwned(latest, _addedMask, ownedBefore);
        if (target != latest && (!TryWriteExtendedStyle(target) || !ApplyFrameChanged()))
        {
            return false;
        }

        return TryReadExtendedStyle(out var verified)
            && (verified & _addedMask) == ownedBefore;
    }

    private void SynchronizeEffectiveState()
    {
        if (!TryReadExtendedStyle(out var current))
        {
            return;
        }

        var state = GhostStylePolicy.EvaluateEffectiveState(current, _addedMask, _isEnabled);
        _isEnabled = state.IsFullyEnabled;
        _requiresCleanup = state.RequiresCleanup;
        SetWpfInputPolicy(_isEnabled || _requiresCleanup);
    }

    private void SetWpfInputPolicy(bool ghost)
    {
        _root.IsHitTestVisible = ghost ? false : _originalIsHitTestVisible;
        _root.Focusable = ghost ? false : _originalFocusable;
        _window.Focusable = ghost ? false : _originalWindowFocusable;
        if (ghost)
        {
            Keyboard.ClearFocus();
            Mouse.Capture(null);
        }
    }

    private bool TryRestoreExternalForeground(IntPtr foregroundToRestore)
    {
        if (!IsExternalWindow(foregroundToRestore))
        {
            return GetForegroundWindow() != _handle;
        }

        if (GetForegroundWindow() == foregroundToRestore)
        {
            return true;
        }

        return SetForegroundWindow(foregroundToRestore)
            && GetForegroundWindow() == foregroundToRestore;
    }

    private bool IsExternalWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero
            || handle == _handle
            || !IsWindow(handle)
            || !IsWindowVisible(handle)
            || GetAncestor(handle, 2) != handle)
        {
            return false;
        }

        GetWindowThreadProcessId(handle, out var processId);
        return processId != (uint)Environment.ProcessId;
    }

    private bool ApplyFrameChanged()
    {
        return SetWindowPos(
            _handle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    private bool TryReadExtendedStyle(out long style)
    {
        style = 0;
        if (_handle == IntPtr.Zero)
        {
            return false;
        }

        SetLastError(0);
        var value = IntPtr.Size == 8
            ? GetWindowLongPtr(_handle, GwlExStyle)
            : new IntPtr(GetWindowLong(_handle, GwlExStyle));
        if (value == IntPtr.Zero && Marshal.GetLastWin32Error() != 0)
        {
            return false;
        }

        style = value.ToInt64();
        return true;
    }

    private bool TryWriteExtendedStyle(long style)
    {
        SetLastError(0);
        var value = new IntPtr(style);
        var previous = IntPtr.Size == 8
            ? SetWindowLongPtr(_handle, GwlExStyle, value)
            : new IntPtr(SetWindowLong(_handle, GwlExStyle, value.ToInt32()));
        return previous != IntPtr.Zero || Marshal.GetLastWin32Error() == 0;
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((IsInputSuppressed || _managementBypass) && message == WmNchittest)
        {
            handled = true;
            return new IntPtr(HtTransparent);
        }

        if ((IsInputSuppressed || _managementBypass) && message == WmMouseactivate)
        {
            handled = true;
            return new IntPtr(MaNoactivate);
        }

        return IntPtr.Zero;
    }

    private void EnsureAttached()
    {
        if (_source is null)
        {
            Attach();
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void SetLastError(int errorCode);
}
