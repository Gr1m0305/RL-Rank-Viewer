using System.Runtime.InteropServices;
using RLOverlay.Core.Config;

namespace RLOverlay.App.Native;

internal static class MonitorInfo
{
    /// <summary>Bounds (physical pixels) of the monitor containing <paramref name="hwnd"/>, or the primary monitor.</summary>
    public static PixelRect BoundsFor(IntPtr hwnd)
    {
        var monitor = hwnd != IntPtr.Zero
            ? NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST)
            : NativeMethods.MonitorFromPoint(default, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        return Bounds(monitor);
    }

    public static PixelRect Primary() =>
        Bounds(NativeMethods.MonitorFromPoint(default, NativeMethods.MONITOR_DEFAULTTOPRIMARY));

    private static PixelRect Bounds(IntPtr monitor)
    {
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return new PixelRect(0, 0, 1920, 1080);
        }

        var r = info.rcMonitor;
        return new PixelRect(r.Left, r.Top, r.Width, r.Height);
    }
}
