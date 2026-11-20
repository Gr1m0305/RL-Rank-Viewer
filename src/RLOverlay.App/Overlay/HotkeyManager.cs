using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using RLOverlay.App.Native;
using RLOverlay.Core.Config;

namespace RLOverlay.App.Overlay;

public enum OverlayHotkey
{
    TogglePanel = 0xB001,
    CyclePlaylist = 0xB002,
    Favourites = 0xB003,
}

/// <summary>
/// Global hotkeys via <c>RegisterHotKey</c> on the overlay's own window. They are registered only while active
/// (normally: while Rocket League is the foreground window), so common combos like Ctrl+R stay free for browsers.
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly ILogger _logger;
    private readonly Dictionary<OverlayHotkey, HotkeyGesture> _bindings = new();
    private readonly HashSet<OverlayHotkey> _registered = [];
    private bool _active;

    public HotkeyManager(HwndSource source, ILogger<HotkeyManager> logger)
    {
        _source = source;
        _logger = logger;
        _source.AddHook(WndProc);
    }

    public event Action<OverlayHotkey>? Pressed;

    public bool IsActive => _active;

    /// <summary>Bindings that could not be registered (taken by another app or invalid), for the settings UI.</summary>
    public IReadOnlyList<string> Failures { get; private set; } = [];

    public void SetBindings(HotkeyGesture toggle, HotkeyGesture cycle, HotkeyGesture favourites)
    {
        var wasActive = _active;
        SetActive(false);
        _bindings[OverlayHotkey.TogglePanel] = toggle;
        _bindings[OverlayHotkey.CyclePlaylist] = cycle;
        _bindings[OverlayHotkey.Favourites] = favourites;
        SetActive(wasActive);
    }

    public void SetActive(bool active)
    {
        if (active == _active)
        {
            return;
        }

        _active = active;
        if (active)
        {
            RegisterAll();
        }
        else
        {
            UnregisterAll();
        }
    }

    /// <summary>Converts a gesture's key name to a Win32 virtual-key code (0 if it is not a valid WPF key).</summary>
    public static uint VirtualKey(HotkeyGesture gesture) =>
        Enum.TryParse<Key>(gesture.Key, ignoreCase: true, out var key) && key != Key.None
            ? (uint)KeyInterop.VirtualKeyFromKey(key)
            : 0;

    private void RegisterAll()
    {
        var failures = new List<string>();
        foreach (var (id, gesture) in _bindings)
        {
            var vk = VirtualKey(gesture);
            if (vk == 0)
            {
                failures.Add($"{gesture}: not a valid key");
                continue;
            }

            if (NativeMethods.RegisterHotKey(_source.Handle, (int)id, (uint)gesture.Modifiers | NativeMethods.MOD_NOREPEAT, vk))
            {
                _registered.Add(id);
            }
            else
            {
                var error = Marshal.GetLastPInvokeError();
                failures.Add($"{gesture}: already used by another app (error {error})");
                _logger.LogWarning("Could not register hotkey {Gesture} (Win32 error {Error})", gesture, error);
            }
        }

        Failures = failures;
        _logger.LogDebug("Hotkeys registered: {Hotkeys}", string.Join(", ", _registered.Select(i => _bindings[i])));
    }

    private void UnregisterAll()
    {
        foreach (var id in _registered)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, (int)id);
        }

        _registered.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && Enum.IsDefined(typeof(OverlayHotkey), wParam.ToInt32()))
        {
            handled = true;
            Pressed?.Invoke((OverlayHotkey)wParam.ToInt32());
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
    }
}
