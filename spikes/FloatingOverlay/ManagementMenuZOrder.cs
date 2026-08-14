using System.Runtime.InteropServices;

namespace LLMLimitsWidget.FloatingOverlay;

internal static class ManagementMenuZOrder
{
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    public static bool EnsureAboveOverlay(IntPtr handle)
    {
        return handle != IntPtr.Zero
            && SetWindowPos(
                handle,
                HwndTopmost,
                0,
                0,
                0,
                0,
                SwpNoSize | SwpNoMove | SwpNoActivate);
    }

    public static bool ShouldHideOverlayForRecovery(bool overlayDemoted, bool menuRaised)
    {
        return !overlayDemoted && !menuRaised;
    }

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
}
