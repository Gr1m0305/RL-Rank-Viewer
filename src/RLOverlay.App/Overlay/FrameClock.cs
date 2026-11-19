using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace RLOverlay.App.Overlay;

/// <summary>
/// Calls an action once per refresh of the monitor a window is on, from a background thread, right after the monitor's
/// vertical blank (<c>D3DKMTWaitForVerticalBlankEvent</c>, the display driver's own signal; nothing to do with the
/// game). If the driver can't be asked, a high-resolution timer at the monitor's refresh rate stands in. Used while
/// the panel is dragged, so it moves once per frame the screen shows instead of on a coarse UI timer.
/// </summary>
internal sealed class FrameClock : IDisposable
{
    private const double FallbackHz = 240;
    private static readonly TimeSpan StalledWait = TimeSpan.FromMilliseconds(250);

    private readonly Action _tick;
    private readonly IntPtr _monitor;
    private readonly ILogger? _logger;
    private volatile bool _stopped;

    private FrameClock(IntPtr monitor, Action tick, ILogger? logger)
    {
        _monitor = monitor;
        _tick = tick;
        _logger = logger;
    }

    /// <summary>Starts ticking for the monitor <paramref name="hwnd"/> is on until disposed.</summary>
    public static FrameClock Start(IntPtr hwnd, Action tick, ILogger? logger = null)
    {
        var clock = new FrameClock(Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST), tick, logger);
        var thread = new Thread(clock.Run)
        {
            IsBackground = true,
            Name = "Overlay frame clock",
            Priority = ThreadPriority.Highest, // it only wakes up, reads the time and posts; the work runs on the UI thread
        };
        thread.Start();
        return clock;
    }

    /// <summary>What the clock waits on and at what rate (diagnostics).</summary>
    public string Source { get; private set; } = "starting";

    /// <summary>Stops ticking. Doesn't wait for the thread: a tick in progress may be waiting for the UI thread.</summary>
    public void Dispose() => _stopped = true;

    private void Run()
    {
        using var vblank = VBlank.TryOpen(_monitor, out var hz);
        using var timer = vblank is null ? FallbackTimer.Create() : null;
        var period = TimeSpan.FromSeconds(1 / (hz is > 20 and < 1000 ? hz : FallbackHz));
        Source = vblank is not null ? $"vblank {hz:0} Hz" : $"timer {1 / period.TotalSeconds:0} Hz";
        var useVBlank = vblank is not null;
        while (!_stopped)
        {
            var waited = System.Diagnostics.Stopwatch.GetTimestamp();
            if (useVBlank && (!vblank!.Wait() || System.Diagnostics.Stopwatch.GetElapsedTime(waited) > StalledWait))
            {
                // The driver stopped answering or kept us waiting (monitor off, display change): carry on with the timer.
                useVBlank = false;
                Source = $"timer {1 / period.TotalSeconds:0} Hz (vblank failed)";
                _logger?.LogDebug("Frame clock: vertical blank wait failed or stalled, using a timer");
            }

            if (!useVBlank)
            {
                (timer ?? FallbackTimer.Shared).Wait(period);
            }

            if (_stopped)
            {
                break;
            }

            try
            {
                _tick();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Frame clock tick failed");
            }
        }
    }

    /// <summary>The display driver's vertical blank event for one monitor.</summary>
    private sealed class VBlank : IDisposable
    {
        private readonly uint _adapter;
        private readonly uint _source;

        private VBlank(uint adapter, uint source)
        {
            _adapter = adapter;
            _source = source;
        }

        public static VBlank? TryOpen(IntPtr monitor, out double hz)
        {
            hz = 0;
            var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (monitor == IntPtr.Zero || !Native.GetMonitorInfo(monitor, ref info))
            {
                return null;
            }

            var dc = Native.CreateDC(null, info.szDevice, null, IntPtr.Zero);
            if (dc == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                hz = Native.GetDeviceCaps(dc, Native.VREFRESH);
                var open = new Native.D3DKMT_OPENADAPTERFROMHDC { hDc = dc };
                return Native.D3DKMTOpenAdapterFromHdc(ref open) == 0 ? new VBlank(open.hAdapter, open.VidPnSourceId) : null;
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
            {
                return null;
            }
            finally
            {
                Native.DeleteDC(dc);
            }
        }

        public bool Wait()
        {
            var wait = new Native.D3DKMT_WAITFORVERTICALBLANKEVENT { hAdapter = _adapter, VidPnSourceId = _source };
            return Native.D3DKMTWaitForVerticalBlankEvent(ref wait) == 0;
        }

        public void Dispose()
        {
            var close = new Native.D3DKMT_CLOSEADAPTER { hAdapter = _adapter };
            Native.D3DKMTCloseAdapter(ref close);
        }
    }

    /// <summary>A waitable timer with sub-millisecond resolution (Windows 10 1803+; a normal one before that).</summary>
    private sealed class FallbackTimer : IDisposable
    {
        private readonly IntPtr _handle;

        private FallbackTimer(IntPtr handle) => _handle = handle;

        public static FallbackTimer Shared { get; } = Create();

        public static FallbackTimer Create()
        {
            var handle = Native.CreateWaitableTimerExW(IntPtr.Zero, null, Native.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Native.TIMER_ALL_ACCESS);
            if (handle == IntPtr.Zero)
            {
                handle = Native.CreateWaitableTimerExW(IntPtr.Zero, null, 0, Native.TIMER_ALL_ACCESS);
            }

            return new FallbackTimer(handle);
        }

        public void Wait(TimeSpan period)
        {
            var due = -period.Ticks; // relative, in 100 ns units
            if (_handle == IntPtr.Zero || !Native.SetWaitableTimer(_handle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                Thread.Sleep(period);
                return;
            }

            Native.WaitForSingleObject(_handle, 1000);
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero && this != Shared)
            {
                Native.CloseHandle(_handle);
            }
        }
    }

    private static class Native
    {
        public const uint MONITOR_DEFAULTTONEAREST = 2;
        public const int VREFRESH = 116;
        public const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2;
        public const uint TIMER_ALL_ACCESS = 0x1F0003;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3DKMT_OPENADAPTERFROMHDC
        {
            public IntPtr hDc;
            public uint hAdapter;
            public LUID AdapterLuid;
            public uint VidPnSourceId;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3DKMT_WAITFORVERTICALBLANKEVENT
        {
            public uint hAdapter;
            public uint hDevice;
            public uint VidPnSourceId;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3DKMT_CLOSEADAPTER
        {
            public uint hAdapter;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateDCW")]
        public static extern IntPtr CreateDC(string? driver, string device, string? output, IntPtr initData);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        public static extern int GetDeviceCaps(IntPtr dc, int index);

        [DllImport("gdi32.dll")]
        public static extern int D3DKMTOpenAdapterFromHdc(ref D3DKMT_OPENADAPTERFROMHDC open);

        [DllImport("gdi32.dll")]
        public static extern int D3DKMTWaitForVerticalBlankEvent(ref D3DKMT_WAITFORVERTICALBLANKEVENT wait);

        [DllImport("gdi32.dll")]
        public static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER close);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);

        [DllImport("kernel32.dll")]
        public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
