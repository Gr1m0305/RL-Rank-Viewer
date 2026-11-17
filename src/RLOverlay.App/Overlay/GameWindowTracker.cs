using System.Diagnostics;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using RLOverlay.App.Native;

namespace RLOverlay.App.Overlay;

/// <summary>
/// Knows where the Rocket League window is and whether it is in the foreground, without touching the game process:
/// it listens to <c>EVENT_SYSTEM_FOREGROUND</c> (out-of-context WinEvent), checks the window class/title, and falls
/// back to matching the window's process id against the system process list snapshot (no process handle is opened).
/// Also detects exclusive fullscreen via <c>SHQueryUserNotificationState</c>.
/// </summary>
internal sealed class GameWindowTracker : IDisposable
{
    public const string GameProcessName = "RocketLeague";
    private const string UnrealWindowClass = "LaunchUnrealUWindowsClient";

    private readonly ILogger _logger;
    private readonly NativeMethods.WinEventDelegate _callback; // keep alive for the hook's lifetime
    private readonly DispatcherTimer _timer;
    private IntPtr _hook;
    private HashSet<uint> _gamePids = [];
    private DateTime _pidsRefreshedAt = DateTime.MinValue;
    private bool _fullscreenReported;
    private bool? _wasRunning;

    public GameWindowTracker(ILogger<GameWindowTracker> logger)
    {
        _logger = logger;
        _callback = OnWinEvent;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();
    }

    /// <summary>Raised with true when Rocket League becomes the foreground window, false when it stops being it.</summary>
    public event Action<bool>? GameForegroundChanged;

    /// <summary>
    /// Raised with true when a Rocket League process appears and false when the last one is gone (checked every 2 s
    /// from the process list; never raised for the state found at <see cref="Start"/>).
    /// </summary>
    public event Action<bool>? GameRunningChanged;

    /// <summary>Raised (once per app session) when Rocket League is foreground in exclusive fullscreen.</summary>
    public event Action? ExclusiveFullscreenDetected;

    public bool IsGameForeground { get; private set; }

    /// <summary>Last known Rocket League top-level window (may be zero).</summary>
    public IntPtr GameWindow { get; private set; }

    public bool IsGameRunning
    {
        get
        {
            RefreshPids(force: false);
            return _gamePids.Count > 0;
        }
    }

    public void Start()
    {
        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _callback, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
        if (_hook == IntPtr.Zero)
        {
            _logger.LogWarning("SetWinEventHook failed; falling back to polling the foreground window");
        }

        Refresh();
        _timer.Start();
    }

    /// <summary>Finds the game window even when it is not in the foreground (for monitor placement).</summary>
    public IntPtr FindGameWindow()
    {
        if (GameWindow != IntPtr.Zero && NativeMethods.IsWindow(GameWindow))
        {
            return GameWindow;
        }

        RefreshPids(force: true);
        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (NativeMethods.IsWindowVisible(hwnd) && IsGameWindow(hwnd))
            {
                found = hwnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        GameWindow = found;
        return found;
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time) =>
        Evaluate(hwnd);

    private void Refresh()
    {
        RefreshPids(force: true);
        var running = _gamePids.Count > 0;
        if (running != _wasRunning)
        {
            var first = _wasRunning is null;
            _wasRunning = running;
            if (!first)
            {
                _logger.LogInformation("Rocket League {State}", running ? "started" : "closed");
                if (!running)
                {
                    GameWindow = IntPtr.Zero;
                }

                GameRunningChanged?.Invoke(running);
            }
        }

        Evaluate(NativeMethods.GetForegroundWindow());
        if (IsGameForeground)
        {
            CheckExclusiveFullscreen();
        }
    }

    private void Evaluate(IntPtr foreground)
    {
        var isGame = foreground != IntPtr.Zero && IsGameWindow(foreground);
        if (isGame)
        {
            GameWindow = foreground;
        }

        if (isGame == IsGameForeground)
        {
            return;
        }

        IsGameForeground = isGame;
        _logger.LogDebug("Rocket League foreground: {Foreground}", isGame);
        GameForegroundChanged?.Invoke(isGame);
        if (isGame)
        {
            CheckExclusiveFullscreen();
        }
    }

    private bool IsGameWindow(IntPtr hwnd)
    {
        if (NativeMethods.ClassName(hwnd) == UnrealWindowClass
            && NativeMethods.WindowTitle(hwnd).StartsWith("Rocket League", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0)
        {
            return false;
        }

        RefreshPids(force: false);
        return _gamePids.Contains(pid);
    }

    private void RefreshPids(bool force)
    {
        if (!force && DateTime.UtcNow - _pidsRefreshedAt < TimeSpan.FromSeconds(3))
        {
            return;
        }

        // Process list snapshot only (NtQuerySystemInformation); Id is read from the snapshot, no handle is opened.
        var processes = Process.GetProcessesByName(GameProcessName);
        _gamePids = processes.Select(p => (uint)p.Id).ToHashSet();
        foreach (var p in processes)
        {
            p.Dispose();
        }

        _pidsRefreshedAt = DateTime.UtcNow;
    }

    private void CheckExclusiveFullscreen()
    {
        if (_fullscreenReported)
        {
            return;
        }

        if (NativeMethods.SHQueryUserNotificationState(out var state) == 0
            && state == NativeMethods.QueryUserNotificationState.RunningD3DFullScreen)
        {
            _fullscreenReported = true;
            _logger.LogWarning("Rocket League appears to be in exclusive fullscreen; the overlay cannot draw over it");
            ExclusiveFullscreenDetected?.Invoke();
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
