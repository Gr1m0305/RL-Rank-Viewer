using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using RLOverlay.App.Native;
using RLOverlay.Core.Config;

namespace RLOverlay.App.Overlay;

/// <summary>
/// The transparent, always-on-top, never-activating overlay window. Click-through except over player names
/// (see <see cref="ClickThroughController"/>). Fades/slides in and out, and positions itself top-centre on the
/// game's monitor.
/// </summary>
public partial class OverlayWindow : Window
{
    public const double ShadowMargin = 24;
    public static readonly Thickness ShadowMarginThickness = new(ShadowMargin);

    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(250);

    private readonly ClickThroughController _clickThrough;
    private IntPtr _hwnd;
    private Func<IntPtr>? _gameWindow;
    private double _offsetX;
    private double _offsetY;
    private bool _wantVisible;

    public OverlayWindow()
    {
        InitializeComponent();
        _clickThrough = new ClickThroughController(this, Root);
        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => Reposition();
        DpiChanged += (_, _) => Reposition();
    }

    /// <summary>True while shown or fading in (false while fading out or hidden).</summary>
    public bool IsShowing => _wantVisible;

    public IntPtr Handle => _hwnd;

    public HwndSource? Source => _hwnd == IntPtr.Zero ? null : HwndSource.FromHwnd(_hwnd);

    public object? Panel
    {
        get => PanelHost.Content;
        set => PanelHost.Content = value;
    }

    public object? Strip
    {
        get => StripHost.Content;
        set => StripHost.Content = value;
    }

    /// <summary>Creates the HWND without showing the window (needed for hotkeys before the first show).</summary>
    public IntPtr EnsureHandle()
    {
        if (_hwnd == IntPtr.Zero)
        {
            _hwnd = new WindowInteropHelper(this).EnsureHandle();
        }

        return _hwnd;
    }

    public void Configure(Func<IntPtr> gameWindow, double offsetX, double offsetY, double scale)
    {
        _gameWindow = gameWindow;
        _offsetX = offsetX;
        _offsetY = offsetY;
        Scale.ScaleX = Scale.ScaleY = Math.Clamp(scale, OverlaySettings.MinScale, OverlaySettings.MaxScale);
        Reposition();
    }

    /// <summary>Panel wanted on screen (lobby state / Ctrl+R).</summary>
    public bool PanelOn { get; private set; }

    /// <summary>Ctrl+G strip wanted on screen.</summary>
    public bool StripOn { get; private set; }

    /// <summary>Temporarily hidden because the game is running but another window is in front.</summary>
    public bool Suppressed { get; private set; }

    public void SetPanelVisible(bool on)
    {
        PanelOn = on;
        UpdateVisibility();
    }

    public void SetStripVisible(bool on)
    {
        StripOn = on;
        if (on)
        {
            StripHost.Visibility = Visibility.Visible;
        }

        var fade = new DoubleAnimation(on ? 1 : 0, new Duration(on ? TimeSpan.FromMilliseconds(140) : FadeOut));
        if (!on)
        {
            fade.Completed += (_, _) =>
            {
                if (!StripOn)
                {
                    StripHost.Visibility = Visibility.Hidden; // keeps its space so the window does not resize
                }
            };
        }

        StripHost.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        UpdateVisibility();
    }

    public void SetSuppressed(bool suppressed)
    {
        Suppressed = suppressed;
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        var show = (PanelOn || StripOn) && !Suppressed;
        if (show)
        {
            // Strip-only (Ctrl+G while the panel is hidden): collapse the panel. While the window fades out the panel
            // keeps its layout, so the fade is not cut short.
            PanelHost.Visibility = PanelOn ? Visibility.Visible : Visibility.Collapsed;
            ShowAnimated();
        }
        else
        {
            HideAnimated();
        }
    }

    public void ShowAnimated()
    {
        if (_wantVisible)
        {
            return;
        }

        _wantVisible = true;
        if (!IsVisible)
        {
            Show();
        }

        Reposition();
        EnsureTopmost();
        Animate(to: 1, slideFrom: -10, FadeIn, completed: null);
        _clickThrough.Start();
    }

    public void HideAnimated()
    {
        if (!_wantVisible)
        {
            return;
        }

        _wantVisible = false;
        _clickThrough.Stop();
        Animate(to: 0, slideFrom: null, FadeOut, completed: () =>
        {
            if (!_wantVisible)
            {
                Hide();
            }
        });
    }

    /// <summary>Re-asserts HWND_TOPMOST (borderless games can end up above us after focus changes).</summary>
    public void EnsureTopmost()
    {
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
        }
    }

    /// <summary>Top-centre of the game's monitor (primary monitor if the game is not found), plus the user offset.</summary>
    public void Reposition()
    {
        if (_hwnd == IntPtr.Zero || ActualWidth <= 0)
        {
            return;
        }

        var game = _gameWindow?.Invoke() ?? IntPtr.Zero;
        var monitor = game != IntPtr.Zero ? MonitorInfo.BoundsFor(game) : MonitorInfo.BoundsFor(IntPtr.Zero);
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var width = (int)Math.Ceiling(ActualWidth * dpi);
        var height = (int)Math.Ceiling(ActualHeight * dpi);
        var (x, y) = PanelPlacement.TopCenter(monitor, width, height, _offsetX, _offsetY, dpi, (int)Math.Round(ShadowMargin * dpi));
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetExtendedStyle(_hwnd);
        style |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE
                 | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOPMOST;
        NativeMethods.SetExtendedStyle(_hwnd, style);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        _clickThrough.Attach(_hwnd);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            // Clicking a name must never take focus away from the game.
            handled = true;
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }

        return IntPtr.Zero;
    }

    private void Animate(double to, double? slideFrom, TimeSpan duration, Action? completed)
    {
        var ease = new CubicEase { EasingMode = to > 0 ? EasingMode.EaseOut : EasingMode.EaseIn };
        var fade = new DoubleAnimation(to, new Duration(duration)) { EasingFunction = ease };
        if (completed is not null)
        {
            fade.Completed += (_, _) => completed();
        }

        Root.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);

        var slide = slideFrom is { } from
            ? new DoubleAnimation(from, 0, new Duration(duration)) { EasingFunction = ease }
            : new DoubleAnimation(-6, new Duration(duration)) { EasingFunction = ease };
        Slide.BeginAnimation(TranslateTransform.YProperty, slide, HandoffBehavior.SnapshotAndReplace);
    }
}
